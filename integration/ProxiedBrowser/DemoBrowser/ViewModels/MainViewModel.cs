using System.Collections.ObjectModel;
using System.Net;
using DemoBrowser.Services;

namespace DemoBrowser.ViewModels;

/// <summary>Top-level state for the main window: the tab collection, the active tab and the toolbar commands.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly BrowserEnvironment _environment;
    private readonly CertificateService _certificateService;
    private readonly SettingsService _settingsService;
    private TabViewModel? _activeTab;
    private string _addressText = "";
    private string? _warningMessage;
    private Task? _caCheck;
    private DateTime _lastCaCheck = DateTime.MinValue;
    private bool _restartRequested;

    /// <summary>A page that keeps failing must not hammer the CA URL; one re-check per interval is plenty.</summary>
    private static readonly TimeSpan CaCheckInterval = TimeSpan.FromSeconds(20);

    public MainViewModel(
        BrowserEnvironment environment,
        CertificateService certificateService,
        SettingsService settingsService,
        ProxyDiagnostics diagnostics)
    {
        _environment = environment;
        _certificateService = certificateService;
        _settingsService = settingsService;
        Diagnostics = diagnostics;

        BackCommand = new RelayCommand(() => ActiveTab?.GoBack(), () => ActiveTab?.CanGoBack == true);
        ForwardCommand = new RelayCommand(() => ActiveTab?.GoForward(), () => ActiveTab?.CanGoForward == true);
        ReloadOrStopCommand = new RelayCommand(() =>
        {
            if (ActiveTab is null)
            {
                return;
            }

            if (ActiveTab.IsLoading)
            {
                ActiveTab.Stop();
            }
            else
            {
                ActiveTab.Reload();
            }
        }, () => ActiveTab is not null);
        DevToolsCommand = new RelayCommand(() => ActiveTab?.ToggleDevTools(), () => ActiveTab is not null);
        NewTabCommand = new RelayCommand(() => _ = OpenNewTabAsync());
        CloseTabCommand = new RelayCommand(p => CloseTab(p as TabViewModel));
        ActivateTabCommand = new RelayCommand(p => ActiveTab = p as TabViewModel ?? ActiveTab);
        NavigateCommand = new RelayCommand(NavigateFromAddressBar);
        DismissWarningCommand = new RelayCommand(() => WarningMessage = null);
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    /// <summary>The proxy/certificate log shown by <see cref="Views.DiagnosticsWindow"/> (Ctrl+Shift+D).</summary>
    public ProxyDiagnostics Diagnostics { get; }

    /// <summary>The engine the tabs run on; the diagnostics window reads the switches it was started with.</summary>
    public BrowserEnvironment Environment => _environment;

    /// <summary>Holds the proxy CA the diagnostics window reports on.</summary>
    public CertificateService CertificateService => _certificateService;

    /// <summary>Raised when the last tab is closed; the window closes the application.</summary>
    public event Action? LastTabClosed;

    /// <summary>Raised whenever the URL shown in the address bar should change (active tab switched or navigated).</summary>
    public event Action<string>? ActiveSourceChanged;

    /// <summary>
    /// Raised when the browser engine has to be re-initialised (proxy/CA settings changed, or the proxy's CA was
    /// re-issued). The application answers by restarting itself in flight with the current tabs.
    /// </summary>
    public event Action<string>? RestartRequested;

    /// <summary>Raised after the user opened a new tab (Ctrl+T / "+"): the window puts the focus into the address bar.</summary>
    public event Action<TabViewModel>? NewTabOpened;

    public TabViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            if (ReferenceEquals(_activeTab, value))
            {
                return;
            }

            if (_activeTab is not null)
            {
                _activeTab.IsActive = false;
                _activeTab.PropertyChanged -= OnActiveTabPropertyChanged;
            }

            _activeTab = value;

            if (_activeTab is not null)
            {
                _activeTab.IsActive = true;
                _activeTab.PropertyChanged += OnActiveTabPropertyChanged;
                ActiveSourceChanged?.Invoke(_activeTab.Source);
            }

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsActiveTabLoading));
            OnPropertyChanged(nameof(ActiveStatusText));
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsActiveTabLoading => ActiveTab?.IsLoading == true;

    /// <summary>Status-bar text of the active tab (link under the mouse etc.).</summary>
    public string ActiveStatusText => ActiveTab?.StatusText ?? "";

    public bool HasStatusText => !string.IsNullOrEmpty(ActiveStatusText);

    /// <summary>Two-way bound to the address bar TextBox.</summary>
    public string AddressText
    {
        get => _addressText;
        set => SetProperty(ref _addressText, value);
    }

    /// <summary>Non-blocking warning shown in a banner (e.g. CA download failed). Null hides the banner.</summary>
    public string? WarningMessage
    {
        get => _warningMessage;
        set
        {
            if (SetProperty(ref _warningMessage, value))
            {
                OnPropertyChanged(nameof(HasWarning));
            }
        }
    }

    public bool HasWarning => !string.IsNullOrEmpty(WarningMessage);

    public RelayCommand BackCommand { get; }

    public RelayCommand ForwardCommand { get; }

    public RelayCommand ReloadOrStopCommand { get; }

    /// <summary>Opens/closes Chromium's own DevTools window for the active tab (F12 / Ctrl+Shift+I).</summary>
    public RelayCommand DevToolsCommand { get; }

    public RelayCommand NewTabCommand { get; }

    public RelayCommand CloseTabCommand { get; }

    public RelayCommand ActivateTabCommand { get; }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand DismissWarningCommand { get; }

    /// <summary>Creates a tab, adds it to the collection (the window adds its browser control to the host grid) and starts navigation.</summary>
    public async Task<TabViewModel> OpenTabAsync(string url, bool activate)
    {
        var tab = new TabViewModel(_certificateService);
        tab.NewTabRequested += (sender, uri) => _ = OpenTabAsync(uri, activate: true);
        tab.CertificateProblem += OnCertificateProblem;
        Tabs.Add(tab);
        if (activate || ActiveTab is null)
        {
            ActiveTab = tab;
        }

        await tab.InitializeAsync(_environment, url);
        return tab;
    }

    public void CloseTab(TabViewModel? tab)
    {
        if (tab is null || !Tabs.Contains(tab))
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (ReferenceEquals(ActiveTab, tab))
        {
            ActiveTab = Tabs.Count == 0 ? null : Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        tab.Dispose();

        if (Tabs.Count == 0)
        {
            LastTabClosed?.Invoke();
        }
    }

    /// <summary>Every launch starts fresh with a single tab on the configured start page.</summary>
    public Task OpenStartPageAsync() => OpenTabAsync(_settingsService.Current.StartPage, activate: true);

    /// <summary>
    /// Reopens the tabs an in-flight restart handed over (<see cref="Models.RestartState"/>); falls back to the
    /// start page if the list is empty.
    /// </summary>
    public async Task OpenTabsAsync(IReadOnlyList<string> urls, int activeIndex)
    {
        if (urls.Count == 0)
        {
            await OpenStartPageAsync();
            return;
        }

        var tabs = new List<TabViewModel>(urls.Count);
        foreach (var url in urls)
        {
            tabs.Add(await OpenTabAsync(url, activate: false));
        }

        ActiveTab = tabs[Math.Clamp(activeIndex, 0, tabs.Count - 1)];
    }

    /// <summary>
    /// User-opened tab (Ctrl+T / "+"): opens the start page and hands the focus to the address bar, selected, so
    /// a URL can be typed straight away. The tab keeps its hands off the focus until the user navigates or clicks.
    /// </summary>
    public async Task OpenNewTabAsync()
    {
        var startPage = _settingsService.Current.StartPage;
        var tab = new TabViewModel(_certificateService) { SuppressNavigationFocus = true };
        tab.NewTabRequested += (sender, uri) => _ = OpenTabAsync(uri, activate: true);
        tab.CertificateProblem += OnCertificateProblem;
        Tabs.Add(tab);
        ActiveTab = tab;
        AddressText = startPage;
        NewTabOpened?.Invoke(tab);
        await tab.InitializeAsync(_environment, startPage);
    }

    /// <summary>The URLs of all open tabs and the active one, for handing over to a restarted instance.</summary>
    public (IReadOnlyList<string> Urls, int ActiveIndex) CaptureTabs()
    {
        var urls = Tabs.Select(t => string.IsNullOrEmpty(t.Source) ? _settingsService.Current.StartPage : t.Source).ToList();
        var active = ActiveTab is null ? 0 : Math.Max(0, Tabs.IndexOf(ActiveTab));
        return (urls, active);
    }

    /// <summary>Asks the application to restart the browser engine in flight; idempotent.</summary>
    public void RequestRestart(string reason)
    {
        if (_restartRequested)
        {
            return;
        }

        _restartRequested = true;
        Diagnostics.Info("Restart", $"Restarting the browser engine: {reason}");
        RestartRequested?.Invoke(reason);
    }

    /// <summary>
    /// A certificate that no longer chains to the CA this instance was started with, or a failed proxy tunnel.
    /// Both are what a re-issued proxy CA looks like from inside the engine, and the pins that would accept the
    /// new CA are command-line switches — so the CA is fetched again and, if it really changed, the engine is
    /// restarted in flight. A genuinely bad site certificate (CA unchanged) just shows Chromium's error page.
    /// </summary>
    private void OnCertificateProblem(TabViewModel tab, string reason)
    {
        var settings = _environment.Settings;
        if (!settings.UseProxy || _restartRequested || _caCheck is { IsCompleted: false }
            || DateTime.UtcNow - _lastCaCheck < CaCheckInterval)
        {
            return;
        }

        _lastCaCheck = DateTime.UtcNow;
        Diagnostics.Info("CA", $"Certificate problem ({reason}): re-checking whether the proxy CA changed");
        _caCheck = CheckCaAsync(settings.CaCertUrl);
    }

    private async Task CheckCaAsync(string caCertUrl)
    {
        var changed = await _certificateService.HasCaChangedAsync(caCertUrl);
        if (changed)
        {
            RequestRestart("the proxy CA changed");
        }
    }

    /// <summary>Navigates the active tab to whatever the address bar contains (URL, bare host or search query).</summary>
    public void NavigateFromAddressBar()
    {
        var target = ResolveAddress(AddressText);
        if (target is null)
        {
            return;
        }

        if (ActiveTab is null)
        {
            _ = OpenTabAsync(target, activate: true);
        }
        else
        {
            // The user is done with the address bar: the page may take the focus again.
            ActiveTab.SuppressNavigationFocus = false;
            ActiveTab.Navigate(target);
        }
    }

    /// <summary>
    /// Absolute URI → as-is; bare host (contains a dot, no spaces) → https:// prefixed;
    /// anything else → Google search. (<c>chrome://</c> is Chromium's internal scheme, WebView2's is <c>edge://</c>.)
    /// </summary>
    public static string? ResolveAddress(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && !string.IsNullOrEmpty(absolute.Scheme)
            && (absolute.Scheme is "http" or "https" or "file" or "about" or "chrome" || text.Contains("://", StringComparison.Ordinal)))
        {
            return absolute.ToString();
        }

        if (!text.Contains(' ') && text.Contains('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var hostUri))
        {
            return hostUri.ToString();
        }

        return "https://www.google.com/search?q=" + WebUtility.UrlEncode(text);
    }

    /// <summary>Disposes every tab; the returned task completes once CEF has destroyed all their native browsers.</summary>
    public Task DisposeAllTabs()
    {
        var tabs = Tabs.ToList();
        foreach (var tab in tabs)
        {
            tab.Dispose();
        }

        Tabs.Clear();
        return Task.WhenAll(tabs.Select(t => t.BrowserClosed));
    }

    private void OnActiveTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TabViewModel.Source):
                ActiveSourceChanged?.Invoke(ActiveTab?.Source ?? "");
                break;
            case nameof(TabViewModel.IsLoading):
                OnPropertyChanged(nameof(IsActiveTabLoading));
                break;
            case nameof(TabViewModel.StatusText):
                OnPropertyChanged(nameof(ActiveStatusText));
                OnPropertyChanged(nameof(HasStatusText));
                break;
        }
    }
}
