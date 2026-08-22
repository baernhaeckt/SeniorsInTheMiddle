using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows;
using DemoBrowser.Models;
using DemoBrowser.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DemoBrowser.ViewModels;

/// <summary>
/// One browser tab. Owns its <see cref="WebView2"/> for the tab's whole lifetime: the control is created
/// exactly once, never re-parented, and only its <see cref="UIElement.Visibility"/> is toggled when the
/// active tab changes. Hosting WebView2 in a TabControl DataTemplate would re-create the visual tree on
/// every switch and destroy the CoreWebView2, so that is deliberately avoided.
/// </summary>
public sealed class TabViewModel : ObservableObject, IDisposable
{
    private readonly CertificateService _certificateService;
    private string _title = "New Tab";
    private string _source = "";
    private bool _isActive;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _disposed;
    private bool _lastCertErrorTrustedViaProxy;
    private ConnectionSecurityInfo _securityInfo = new();

    public TabViewModel(CertificateService certificateService)
    {
        _certificateService = certificateService;
        WebView = new WebView2
        {
            Visibility = Visibility.Collapsed,
        };
        WebView.CoreWebView2InitializationCompleted += OnCoreWebView2InitializationCompleted;
        WebView.NavigationStarting += (_, _) => IsLoading = true;
        WebView.NavigationCompleted += OnNavigationCompleted;
        WebView.SourceChanged += (_, _) => SyncSource();
    }

    public WebView2 WebView { get; }

    /// <summary>Raised when the page asks for a new window (target=_blank); the host opens a new tab.</summary>
    public event Action<TabViewModel, string>? NewTabRequested;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Current URL of the tab as a string (kept in sync with the WebView2 Source).</summary>
    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                WebView.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        private set => SetProperty(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        private set => SetProperty(ref _canGoForward, value);
    }

    /// <summary>TLS details of the current page, for the lock-icon popup.</summary>
    public ConnectionSecurityInfo SecurityInfo
    {
        get => _securityInfo;
        private set
        {
            if (SetProperty(ref _securityInfo, value))
            {
                OnPropertyChanged(nameof(SecurityGlyph));
            }
        }
    }

    /// <summary>Lock glyph for the address bar: closed lock (https), open lock (http), warning (broken).</summary>
    public string SecurityGlyph => SecurityInfo.SecurityState switch
    {
        "secure" => "\U0001F512",
        "insecure-broken" => "⚠",
        _ when Source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) => "\U0001F512",
        _ when string.IsNullOrEmpty(Source) => "◎",
        _ => "\U0001F513",
    };

    /// <summary>
    /// Initialises the CoreWebView2 against the single shared environment and navigates to <paramref name="initialUrl"/>.
    /// The certificate handler is attached in <see cref="OnCoreWebView2InitializationCompleted"/>, which runs before
    /// this method continues, so the very first navigation is already covered.
    /// </summary>
    public async Task InitializeAsync(CoreWebView2Environment environment, string initialUrl)
    {
        await WebView.EnsureCoreWebView2Async(environment);
        if (_disposed)
        {
            return;
        }

        Navigate(initialUrl);
    }

    public void Navigate(string url)
    {
        if (WebView.CoreWebView2 is null || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            WebView.CoreWebView2.Navigate(url);
        }
        catch (ArgumentException)
        {
            // Invalid URL: ignore rather than crash the tab.
        }
    }

    public void GoBack()
    {
        if (WebView.CoreWebView2?.CanGoBack == true)
        {
            WebView.CoreWebView2.GoBack();
        }
    }

    public void GoForward()
    {
        if (WebView.CoreWebView2?.CanGoForward == true)
        {
            WebView.CoreWebView2.GoForward();
        }
    }

    public void Reload() => WebView.CoreWebView2?.Reload();

    public void Stop() => WebView.CoreWebView2?.Stop();

    private void OnCoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess || WebView.CoreWebView2 is null)
        {
            Title = "Failed to initialise";
            return;
        }

        var core = WebView.CoreWebView2;
        BrowserEnvironmentService.RegisterBrowserProcess(core.BrowserProcessId);

        // Must be subscribed BEFORE the first navigation, otherwise that navigation's certificate error is missed.
        core.ServerCertificateErrorDetected += (_, args) =>
            _lastCertErrorTrustedViaProxy = _certificateService.HandleServerCertificateError(args);

        core.DocumentTitleChanged += (_, _) => UpdateTitle();
        core.HistoryChanged += (_, _) => UpdateHistoryState();
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            NewTabRequested?.Invoke(this, args.Uri);
        };
        core.Settings.IsStatusBarEnabled = true;

        // TLS details of successful connections are only available through the DevTools protocol.
        core.GetDevToolsProtocolEventReceiver("Security.visibleSecurityStateChanged").DevToolsProtocolEventReceived +=
            (_, args) => OnVisibleSecurityStateChanged(args.ParameterObjectAsJson);
        _ = EnableSecurityDomainAsync(core);
    }

    private static async Task EnableSecurityDomainAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Security.enable", "{}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // DevTools unavailable: the lock popup simply shows less detail.
        }
    }

    /// <summary>Parses the CDP <c>Security.VisibleSecurityState</c> payload into <see cref="SecurityInfo"/>.</summary>
    private void OnVisibleSecurityStateChanged(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("visibleSecurityState", out var state))
            {
                return;
            }

            var chain = new List<X509Certificate2>();
            string protocol = "", keyExchange = "", cipher = "";
            if (state.TryGetProperty("certificateSecurityState", out var cert))
            {
                protocol = cert.TryGetProperty("protocol", out var p) ? p.GetString() ?? "" : "";
                keyExchange = cert.TryGetProperty("keyExchange", out var k) ? k.GetString() ?? "" : "";
                cipher = cert.TryGetProperty("cipher", out var c) ? c.GetString() ?? "" : "";
                if (cert.TryGetProperty("certificate", out var certs) && certs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var entry in certs.EnumerateArray())
                    {
                        var b64 = entry.GetString();
                        if (string.IsNullOrEmpty(b64))
                        {
                            continue;
                        }

                        try
                        {
                            chain.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(b64)));
                        }
                        catch (Exception ex) when (ex is CryptographicException or FormatException)
                        {
                        }
                    }
                }
            }

            var issues = new List<string>();
            if (state.TryGetProperty("securityStateIssueIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                issues.AddRange(ids.EnumerateArray().Select(i => i.GetString() ?? "").Where(i => i.Length > 0));
            }

            var host = Uri.TryCreate(Source, UriKind.Absolute, out var uri) ? uri.Host : "";
            var previous = SecurityInfo;
            SecurityInfo = new ConnectionSecurityInfo
            {
                Host = host,
                SecurityState = state.TryGetProperty("securityState", out var s) ? s.GetString() ?? "" : "",
                Protocol = protocol,
                KeyExchange = keyExchange,
                Cipher = cipher,
                Chain = chain,
                Issues = issues,
                TrustedViaProxyCa = _lastCertErrorTrustedViaProxy || _certificateService.ChainsToProxyCa(chain),
            };
            DisposeChain(previous);
        }
        catch (JsonException)
        {
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        IsLoading = false;
        SyncSource();
        UpdateTitle();
        UpdateHistoryState();
    }

    private void SyncSource()
    {
        var uri = WebView.Source;
        var newSource = uri is null || uri.ToString() == "about:blank" ? "" : uri.ToString();
        if (newSource != Source)
        {
            // New document: forget the previous page's trust decision and glyph.
            _lastCertErrorTrustedViaProxy = false;
            Source = newSource;
            OnPropertyChanged(nameof(SecurityGlyph));
        }

        UpdateTitle();
    }

    /// <summary>Title fallback chain: document title → host → "New Tab".</summary>
    private void UpdateTitle()
    {
        var docTitle = WebView.CoreWebView2?.DocumentTitle;
        if (!string.IsNullOrWhiteSpace(docTitle))
        {
            Title = docTitle;
            return;
        }

        if (Uri.TryCreate(Source, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            Title = uri.Host;
            return;
        }

        Title = "New Tab";
    }

    private void UpdateHistoryState()
    {
        CanGoBack = WebView.CoreWebView2?.CanGoBack ?? false;
        CanGoForward = WebView.CoreWebView2?.CanGoForward ?? false;
        RelayCommand.RaiseCanExecuteChanged();
    }

    private static void DisposeChain(ConnectionSecurityInfo info)
    {
        foreach (var cert in info.Chain)
        {
            cert.Dispose();
        }
    }

    /// <summary>Disposes the WebView2 (and its CoreWebView2) when the tab is closed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeChain(SecurityInfo);
        WebView.Dispose();
    }
}
