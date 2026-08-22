using System.Collections.ObjectModel;
using System.Net;
using DemoBrowser.Models;
using DemoBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace DemoBrowser.ViewModels;

/// <summary>Top-level state for the main window: the tab collection, the active tab and the toolbar commands.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly CoreWebView2Environment _environment;
    private readonly CertificateService _certificateService;
    private readonly SettingsService _settingsService;
    private TabViewModel? _activeTab;
    private string _addressText = "";
    private string? _warningMessage;

    public MainViewModel(CoreWebView2Environment environment, CertificateService certificateService, SettingsService settingsService)
    {
        _environment = environment;
        _certificateService = certificateService;
        _settingsService = settingsService;

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
        NewTabCommand = new RelayCommand(() => _ = OpenTabAsync(_settingsService.Current.StartPage, activate: true));
        CloseTabCommand = new RelayCommand(p => CloseTab(p as TabViewModel));
        ActivateTabCommand = new RelayCommand(p => ActiveTab = p as TabViewModel ?? ActiveTab);
        NavigateCommand = new RelayCommand(NavigateFromAddressBar);
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = [];

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
            RelayCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsActiveTabLoading => ActiveTab?.IsLoading == true;

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

    public RelayCommand NewTabCommand { get; }

    public RelayCommand CloseTabCommand { get; }

    public RelayCommand ActivateTabCommand { get; }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand DismissWarningCommand => new(() => WarningMessage = null);

    /// <summary>Creates a tab, adds it to the collection (the window adds its WebView2 to the host grid) and starts navigation.</summary>
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

    /// <summary>Restores the saved session, or opens the start page if there is nothing to restore.</summary>
    public async Task RestoreSessionAsync(SessionState? session)
    {
        if (session is null || session.TabUrls.Count == 0)
        {
            await OpenTabAsync(_settingsService.Current.StartPage, activate: true);
            return;
        }

        var activeIndex = Math.Clamp(session.ActiveTabIndex, 0, session.TabUrls.Count - 1);
        for (var i = 0; i < session.TabUrls.Count; i++)
        {
            await OpenTabAsync(session.TabUrls[i], activate: i == activeIndex);
        }
    }

    public SessionState CaptureSession() => new()
    {
        TabUrls = Tabs.Select(t => t.Source).Where(s => !string.IsNullOrWhiteSpace(s)).ToList(),
        ActiveTabIndex = ActiveTab is null ? 0 : Math.Max(0, Tabs.IndexOf(ActiveTab)),
    };

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
    /// anything else → Google search.
    /// </summary>
    public static string? ResolveAddress(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && !string.IsNullOrEmpty(absolute.Scheme)
            && (absolute.Scheme is "http" or "https" or "file" or "about" or "edge" || text.Contains("://", StringComparison.Ordinal)))
        {
            return absolute.ToString();
        }

        if (!text.Contains(' ') && text.Contains('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var hostUri))
        {
            return hostUri.ToString();
        }

        return "https://www.google.com/search?q=" + WebUtility.UrlEncode(text);
    }

    public void DisposeAllTabs()
    {
        foreach (var tab in Tabs.ToList())
        {
            tab.Dispose();
        }

        Tabs.Clear();
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
        }
    }
}
