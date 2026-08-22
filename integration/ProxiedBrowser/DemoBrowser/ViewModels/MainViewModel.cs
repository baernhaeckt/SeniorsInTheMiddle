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
        NewTabCommand = new RelayCommand(() => _ = OpenTabAsync(_settingsService.Current.StartPage, activate: true));
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
