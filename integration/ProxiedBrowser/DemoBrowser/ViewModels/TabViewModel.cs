using System.Windows;
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

        // Must be subscribed BEFORE the first navigation, otherwise that navigation's certificate error is missed.
        core.ServerCertificateErrorDetected += (_, args) => _certificateService.HandleServerCertificateError(args);

        core.DocumentTitleChanged += (_, _) => UpdateTitle();
        core.HistoryChanged += (_, _) => UpdateHistoryState();
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            NewTabRequested?.Invoke(this, args.Uri);
        };
        core.Settings.IsStatusBarEnabled = true;
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
        Source = uri is null || uri.ToString() == "about:blank" ? "" : uri.ToString();
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

    /// <summary>Disposes the WebView2 (and its CoreWebView2) when the tab is closed.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WebView.Dispose();
    }
}
