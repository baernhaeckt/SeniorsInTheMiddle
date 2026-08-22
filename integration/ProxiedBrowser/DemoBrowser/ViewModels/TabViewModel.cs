using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using DemoBrowser.Models;
using DemoBrowser.Services;
using Xilium.CefGlue;
using Xilium.CefGlue.Common.Events;
using Xilium.CefGlue.Common.Handlers;

namespace DemoBrowser.ViewModels;

/// <summary>
/// One browser tab. Owns its <see cref="TabBrowser"/> for the tab's whole lifetime: the control is created
/// exactly once, never re-parented, and only its <c>IsVisible</c> is toggled when the active tab changes.
/// Hosting the browser in a TabControl DataTemplate would re-create the visual tree on every switch and
/// destroy the native CEF browser, so that is deliberately avoided.
///
/// CEF raises its callbacks on CEF threads; every property change is marshalled to the Avalonia UI thread.
/// </summary>
public sealed class TabViewModel : ObservableObject, IDisposable
{
    private readonly CertificateService _certificateService;
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _title = "New Tab";
    private string _source = "";
    private string _statusText = "";
    private bool _isActive;
    private bool _isLoading;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _disposed;
    private volatile bool _lastCertErrorTrustedViaProxy;
    private IReadOnlyList<string> _lastCertErrorIssues = [];
    private IReadOnlyList<X509Certificate2> _lastCertErrorChain = [];
    private ConnectionSecurityInfo _securityInfo = new();
    private CertificateService.ProxyEndpoint? _proxyEndpoint;
    private CefRegistration? _devToolsRegistration;

    private const int EnableNetworkMessageId = 1;

    public TabViewModel(CertificateService certificateService)
    {
        _certificateService = certificateService;
        WebView = new TabBrowser
        {
            IsVisible = false,
            // Handlers must be in place before the native browser is created, so the very first
            // navigation's certificate error is already covered.
            RequestHandler = new TabRequestHandler(this),
            LifeSpanHandler = new TabLifeSpanHandler(this),
        };
        WebView.BrowserInitialized += OnBrowserInitialized;
        WebView.LoadStart += OnLoadStart;
        WebView.LoadEnd += OnLoadEnd;
        WebView.LoadingStateChange += OnLoadingStateChange;
        WebView.AddressChanged += (_, url) => Post(() => SyncSource(url));
        WebView.TitleChanged += (_, _) => Post(UpdateTitle);
        WebView.StatusMessage += (_, text) => Post(() => StatusText = text ?? "");
    }

    public TabBrowser WebView { get; }

    /// <summary>
    /// Completes once CEF has fully destroyed the native browser (<c>OnBeforeClose</c>) after <see cref="Dispose"/>.
    /// Closing is asynchronous and needs a running UI message loop, so the window waits for this before the
    /// process shuts the engine down — the CEF counterpart of waiting for WebView2's browser process to exit.
    /// </summary>
    public Task BrowserClosed => _closed.Task;

    /// <summary>Raised when the page asks for a new window (target=_blank); the host opens a new tab.</summary>
    public event Action<TabViewModel, string>? NewTabRequested;

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    /// <summary>Current URL of the tab as a string (kept in sync with the browser's main-frame URL).</summary>
    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    /// <summary>Status-bar text (link target under the mouse, loading hints) as reported by Chromium.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (SetProperty(ref _isActive, value))
            {
                WebView.IsVisible = value;
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
    /// Queues the first navigation and waits until the native browser exists. The browser is created by CEF as
    /// soon as the control is laid out with a size; the initial URL is loaded right after creation. The
    /// certificate handler is attached before creation, so the very first navigation is already covered.
    /// </summary>
    public async Task InitializeAsync(BrowserEnvironment environment, string initialUrl)
    {
        // One process-wide engine: every tab implicitly uses it. Its settings are kept for one thing only —
        // asking the proxy which certificate it minted for a host, which is what the lock popup shows now that
        // Chromium no longer reports an error for those certificates (see OnDocumentResponseReceived).
        _proxyEndpoint = environment.Settings.UseProxy
            ? new CertificateService.ProxyEndpoint(
                environment.Settings.ProxyHost.Trim(),
                environment.Settings.ProxyPort,
                string.Equals(environment.Settings.ProxyScheme, "https", StringComparison.OrdinalIgnoreCase))
            : null;
        Navigate(initialUrl);
        await _initialized.Task;
        if (_disposed)
        {
            return;
        }
    }

    public void Navigate(string url)
    {
        if (_disposed || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            WebView.Address = url;
        }
        catch (ArgumentException)
        {
            // Invalid URL: ignore rather than crash the tab.
        }
    }

    public void GoBack()
    {
        if (WebView.CanGoBack)
        {
            WebView.GoBack();
        }
    }

    public void GoForward()
    {
        if (WebView.CanGoForward)
        {
            WebView.GoForward();
        }
    }

    public void Reload()
    {
        if (WebView.Core is not null)
        {
            WebView.Reload();
        }
    }

    public void Stop() => WebView.Core?.StopLoad();

    /// <summary>Opens the tab's DevTools window, or closes it again when it is already open (F12).</summary>
    public void ToggleDevTools()
    {
        if (!_disposed)
        {
            WebView.ToggleDevTools();
        }
    }

    private void OnBrowserInitialized()
    {
        var core = WebView.Core;
        if (core is null)
        {
            Post(() => Title = "Failed to initialise");
            _initialized.TrySetResult();
            return;
        }

        // TLS details of successful connections are only available through the DevTools protocol.
        TabBrowser.RunOnCefUiThread(() =>
        {
            try
            {
                var host = core.GetHost();
                _devToolsRegistration = host.AddDevToolsMessageObserver(new DevToolsObserver(this));
                SendDevToolsMessage(host, $"{{\"id\":{EnableNetworkMessageId},\"method\":\"Network.enable\"}}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or CefRuntimeException or ObjectDisposedException)
            {
                // DevTools unavailable: the lock popup simply shows less detail.
            }
        });

        _initialized.TrySetResult();
    }

    private static void SendDevToolsMessage(CefBrowserHost host, string json)
    {
        var message = Encoding.UTF8.GetBytes(json);
        var buffer = Marshal.AllocHGlobal(message.Length);
        try
        {
            Marshal.Copy(message, 0, buffer, message.Length);
            host.SendDevToolsMessage(buffer, message.Length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Parses the CDP <c>Network.responseReceived</c> payload of the main document into <see cref="SecurityInfo"/>
    /// and asks for the certificate chain of that origin.
    ///
    /// WHY the Network domain: the WebView2 build reads <c>Security.visibleSecurityStateChanged</c>, but CEF's
    /// runtime never emits the Security domain (its events simply never arrive, while other domains do). The
    /// Network domain carries the same information — <c>securityState</c> plus <c>securityDetails</c> with the
    /// TLS protocol, key exchange and cipher — for every response, including the main document.
    /// </summary>
    private void OnDocumentResponseReceived(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "Document"
                || !root.TryGetProperty("response", out var response))
            {
                return;
            }

            var url = response.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return;
            }

            string protocol = "", keyExchange = "", cipher = "";
            if (response.TryGetProperty("securityDetails", out var details))
            {
                protocol = details.TryGetProperty("protocol", out var p) ? p.GetString() ?? "" : "";
                cipher = details.TryGetProperty("cipher", out var c) ? c.GetString() ?? "" : "";
                keyExchange = details.TryGetProperty("keyExchange", out var k) ? k.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(keyExchange) && details.TryGetProperty("keyExchangeGroup", out var g))
                {
                    // TLS 1.3 has no separate key exchange algorithm; Chromium reports only the group.
                    keyExchange = g.GetString() ?? "";
                }
            }

            var previous = SecurityInfo;
            var chain = _lastCertErrorChain;
            SecurityInfo = new ConnectionSecurityInfo
            {
                Host = uri.Host,
                SecurityState = response.TryGetProperty("securityState", out var s) ? s.GetString() ?? "" : "",
                Protocol = protocol,
                KeyExchange = keyExchange,
                Cipher = cipher,
                Chain = chain,
                Issues = _lastCertErrorIssues,
                TrustedViaProxyCa = _lastCertErrorTrustedViaProxy || _certificateService.ChainsToProxyCa(chain),
            };
            if (!ReferenceEquals(previous.Chain, chain))
            {
                DisposeChain(previous);
            }

            if (chain.Count == 0 && uri.Scheme == Uri.UriSchemeHttps)
            {
                _ = AttachInterceptedCertificateAsync(uri.Host);
            }
        }
        catch (JsonException)
        {
        }
    }

    /// <summary>
    /// Asks the proxy for the certificate it minted for <paramref name="host"/> and puts it in the lock popup.
    ///
    /// WHY this is needed: the chain used to arrive for free, because every site behind the proxy raised a
    /// certificate error and OnCertificateError carries the certificate. It no longer does — the proxy's signing
    /// key is pinned before the engine starts, so Chromium accepts those certificates silently, which is the only
    /// way subresources on other origins can load at all. CEF exposes no way to read the certificate of a
    /// *successful* connection (its DevTools Security domain stays silent and Network.getCertificate answers with
    /// an empty list), so the certificate is fetched the same way the browser got it: through the proxy.
    ///
    /// Best effort and off the navigation path — a lock popup without a chain is a small loss, a blocked
    /// navigation is not.
    /// </summary>
    private async Task AttachInterceptedCertificateAsync(string host)
    {
        if (_proxyEndpoint is not { } endpoint)
        {
            return;
        }

        var certificate = await _certificateService
            .ProbeInterceptedCertificateAsync(endpoint, host)
            .ConfigureAwait(false);
        if (certificate is null)
        {
            return;
        }

        Post(() =>
        {
            var current = SecurityInfo;
            if (_disposed || current.Chain.Count > 0 || !string.Equals(current.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                // Navigated on, or a certificate error filled the chain in the meantime.
                certificate.Dispose();
                return;
            }

            SecurityInfo = new ConnectionSecurityInfo
            {
                Host = current.Host,
                SecurityState = current.SecurityState,
                Protocol = current.Protocol,
                KeyExchange = current.KeyExchange,
                Cipher = current.Cipher,
                Chain = [certificate],
                Issues = current.Issues,
                TrustedViaProxyCa = true,
            };
        });
    }

    private void OnLoadStart(object sender, LoadStartEventArgs e)
    {
        if (e.Frame.IsMain && !e.Frame.Browser.IsPopup)
        {
            Post(() => IsLoading = true);
        }
    }

    private void OnLoadEnd(object sender, LoadEndEventArgs e)
    {
        if (!e.Frame.IsMain || e.Frame.Browser.IsPopup)
        {
            return;
        }

        var url = e.Frame.Url;
        Post(() =>
        {
            IsLoading = false;
            SyncSource(url);
            UpdateTitle();
            UpdateHistoryState();
        });
    }

    /// <summary>CEF reports loading + history state together; this covers NavigationStarting/Completed and HistoryChanged.</summary>
    private void OnLoadingStateChange(object sender, LoadingStateChangeEventArgs e)
    {
        var url = WebView.Core?.GetMainFrame()?.Url;
        Post(() =>
        {
            IsLoading = e.IsLoading;
            CanGoBack = e.CanGoBack;
            CanGoForward = e.CanGoForward;
            RelayCommand.RaiseCanExecuteChanged();
            if (!e.IsLoading)
            {
                SyncSource(url);
                UpdateTitle();
            }
        });
    }

    private void SyncSource(string? url)
    {
        var newSource = url is null || url == "about:blank" ? "" : url;
        if (newSource != Source)
        {
            // New document: forget the previous page's trust decision and glyph.
            _lastCertErrorTrustedViaProxy = false;
            _lastCertErrorIssues = [];
            Source = newSource;
            OnPropertyChanged(nameof(SecurityGlyph));
        }

        UpdateTitle();
    }

    /// <summary>Title fallback chain: document title → host → "New Tab".</summary>
    private void UpdateTitle()
    {
        var docTitle = WebView.Title;
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
        CanGoBack = WebView.CanGoBack;
        CanGoForward = WebView.CanGoForward;
        RelayCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Stores the chain of the latest certificate error, disposing the previous one if unused.</summary>
    private void ReplaceCertErrorChain(IReadOnlyList<X509Certificate2> chain)
    {
        var previous = _lastCertErrorChain;
        _lastCertErrorChain = chain;
        if (!ReferenceEquals(previous, SecurityInfo.Chain))
        {
            foreach (var cert in previous)
            {
                cert.Dispose();
            }
        }
    }

    private static void DisposeChain(ConnectionSecurityInfo info)
    {
        foreach (var cert in info.Chain)
        {
            cert.Dispose();
        }
    }

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                {
                    action();
                }
            });
        }
    }

    /// <summary>Disposes the browser control (and its native CEF browser) when the tab is closed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialized.TrySetResult();
        ReplaceCertErrorChain([]);
        DisposeChain(SecurityInfo);
        _devToolsRegistration?.Dispose();
        _devToolsRegistration = null;
        WebView.CloseDevTools();
        var hadBrowser = WebView.Core is not null;
        WebView.Dispose();
        if (!hadBrowser)
        {
            // No native browser was ever created, so CEF will never report OnBeforeClose for it.
            _closed.TrySetResult();
        }
    }

    /// <summary>
    /// The CEF counterpart of WebView2's <c>ServerCertificateErrorDetected</c>: called (on a CEF thread) for every
    /// certificate Chromium cannot validate itself — with the MITM proxy that is every HTTPS site.
    /// </summary>
    private sealed class TabRequestHandler(TabViewModel owner) : RequestHandler
    {
        protected override bool OnCertificateError(CefBrowser browser, CefErrorCode certError, string requestUrl, CefSslInfo sslInfo, CefCallback callback)
        {
            var (trusted, chain) = owner._certificateService.HandleServerCertificateError(sslInfo, callback);
            owner._lastCertErrorTrustedViaProxy = trusted;
            owner._lastCertErrorIssues = trusted ? [] : DescribeCertStatus(sslInfo?.CertStatus ?? CefCertStatus.None, certError);
            owner.ReplaceCertErrorChain(chain);
            return trusted;
        }

        /// <summary>
        /// Human-readable reasons a certificate was rejected — the equivalent of the Security domain's
        /// <c>securityStateIssueIds</c> the WebView2 build shows in the lock popup.
        /// </summary>
        private static string[] DescribeCertStatus(CefCertStatus status, CefErrorCode error)
        {
            var issues = new List<string>();
            foreach (var (flag, text) in CertStatusText)
            {
                if (status.HasFlag(flag))
                {
                    issues.Add(text);
                }
            }

            if (issues.Count == 0)
            {
                issues.Add(error.ToString());
            }

            return [.. issues];
        }

        private static readonly (CefCertStatus Flag, string Text)[] CertStatusText =
        [
            (CefCertStatus.CommonNameInvalid, "certificate name does not match the site"),
            (CefCertStatus.DateInvalid, "certificate is expired or not yet valid"),
            (CefCertStatus.AuthorityInvalid, "issuer is not trusted"),
            (CefCertStatus.Revoked, "certificate is revoked"),
            (CefCertStatus.Invalid, "certificate is malformed"),
            (CefCertStatus.WeakSignatureAlgorithm, "weak signature algorithm"),
            (CefCertStatus.WeakKey, "weak key"),
            (CefCertStatus.NameConstraintViolation, "name constraint violation"),
            (CefCertStatus.ValidityTooLong, "validity period too long"),
            (CefCertStatus.NonUniqueName, "non-unique host name"),
            (CefCertStatus.PinnedKeyMissing, "expected public key pin missing"),
            (CefCertStatus.Sha1SignaturePresent, "SHA-1 signature present"),
            (CefCertStatus.CTComplianceFailed, "certificate transparency requirements not met"),
        ];
    }

    /// <summary>The CEF counterpart of WebView2's <c>NewWindowRequested</c>: popups are redirected into a new tab.</summary>
    private sealed class TabLifeSpanHandler(TabViewModel owner) : LifeSpanHandler
    {
        protected override bool OnBeforePopup(CefBrowser browser, CefFrame frame, string targetUrl, string targetFrameName,
            CefWindowOpenDisposition targetDisposition, bool userGesture, CefPopupFeatures popupFeatures, CefWindowInfo windowInfo,
            ref CefClient client, CefBrowserSettings settings, ref CefDictionaryValue extraInfo, ref bool noJavascriptAccess)
        {
            var url = targetUrl;
            owner.Post(() => owner.NewTabRequested?.Invoke(owner, url));
            return true; // handled: no native popup window
        }

        protected override void OnBeforeClose(CefBrowser browser)
        {
            if (!browser.IsPopup)
            {
                owner._closed.TrySetResult();
            }

            base.OnBeforeClose(browser);
        }
    }

    /// <summary>
    /// Receives DevTools protocol traffic: the <c>Network.responseReceived</c> event (TLS parameters of the main
    /// document) and the result of the <c>Network.getCertificate</c> call (the PEM chain).
    /// </summary>
    private sealed class DevToolsObserver(TabViewModel owner) : CefDevToolsMessageObserver
    {
        protected override bool OnDevToolsMessage(CefBrowser browser, IntPtr message, int messageSize) => false;

        protected override void OnDevToolsMethodResult(CefBrowser browser, int messageId, bool success, IntPtr result, int resultSize)
        {
        }

        protected override void OnDevToolsEvent(CefBrowser browser, string method, IntPtr parameters, int parametersSize)
        {
            if (method != "Network.responseReceived" || parameters == IntPtr.Zero || parametersSize <= 0)
            {
                return;
            }

            var json = ReadUtf8(parameters, parametersSize);
            owner.Post(() => owner.OnDocumentResponseReceived(json));
        }

        protected override void OnDevToolsAgentAttached(CefBrowser browser)
        {
        }

        protected override void OnDevToolsAgentDetached(CefBrowser browser)
        {
        }

        private static string ReadUtf8(IntPtr pointer, int size)
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, size);
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
