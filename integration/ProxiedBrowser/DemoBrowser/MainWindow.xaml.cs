using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using DemoBrowser.Models;
using DemoBrowser.Services;
using DemoBrowser.ViewModels;
using DemoBrowser.Views;

namespace DemoBrowser;

/// <summary>
/// Main browser window with custom chrome (the tab strip doubles as the title bar).
/// The tab strip is a header-only ItemsControl; every tab's WebView2 is added to <c>WebViewHost</c>
/// exactly once when the tab is created and removed only when the tab closes.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private readonly SessionService _sessionService;
    private bool _sessionSaved;

    public MainWindow(MainViewModel viewModel, SettingsService settingsService, SessionService sessionService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsService = settingsService;
        _sessionService = sessionService;
        DataContext = viewModel;

        viewModel.Tabs.CollectionChanged += OnTabsChanged;
        viewModel.LastTabClosed += Close;
        viewModel.ActiveSourceChanged += OnActiveSourceChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        InputBindings.Add(new KeyBinding(viewModel.NewTabCommand, Key.T, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(() => viewModel.CloseTab(viewModel.ActiveTab)), Key.W, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(FocusAddressBar), Key.L, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(viewModel.ReloadOrStopCommand, Key.F5, ModifierKeys.None));

        StateChanged += (_, _) => ApplyWindowState();
        ApplyWindowState();
    }

    /// <summary>Restores the previous session (or opens the start page). Called once by App after the window is shown.</summary>
    public Task RestoreSessionAsync(SessionState? session) => _viewModel.RestoreSessionAsync(session);

    /// <summary>
    /// With WindowStyle=None a maximized window overhangs the screen edges by the resize border, so we
    /// compensate with a margin; we also swap the maximize/restore glyph.
    /// </summary>
    private void ApplyWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        RootBorder.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (TabViewModel tab in e.NewItems)
            {
                // Added exactly once; lives here until the tab is closed.
                WebViewHost.Children.Add(tab.WebView);
            }
        }

        if (e.OldItems is not null)
        {
            foreach (TabViewModel tab in e.OldItems)
            {
                WebViewHost.Children.Remove(tab.WebView);
            }
        }
    }

    /// <summary>Only overwrite the address bar when the user is not typing in it.</summary>
    private void OnActiveSourceChanged(string source)
    {
        if (!AddressBar.IsKeyboardFocusWithin)
        {
            _viewModel.AddressText = source;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsActiveTabLoading))
        {
            ReloadStopGlyph.Text = _viewModel.IsActiveTabLoading ? "✕" : "↻";
        }
        else if (e.PropertyName is nameof(MainViewModel.ActiveTab))
        {
            ReloadStopGlyph.Text = _viewModel.IsActiveTabLoading ? "✕" : "↻";
            Title = _viewModel.ActiveTab is null ? "Demo Browser" : $"{_viewModel.ActiveTab.Title} - Demo Browser";
        }
    }

    private void OnAddressBarKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _viewModel.NavigateFromAddressBar();
            _viewModel.ActiveTab?.WebView.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.AddressText = _viewModel.ActiveTab?.Source ?? "";
            _viewModel.ActiveTab?.WebView.Focus();
        }
    }

    private void OnAddressBarGotFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBar.SelectAll();

    private void FocusAddressBar()
    {
        AddressBar.Focus();
        AddressBar.SelectAll();
    }

    /// <summary>Lock icon: shows the certificate / connection details of the active tab.</summary>
    private void OnSecurityInfoClick(object sender, RoutedEventArgs e)
    {
        var tab = _viewModel.ActiveTab;
        var info = tab?.SecurityInfo ?? new Models.ConnectionSecurityInfo();
        var dialog = new CertificateInfoWindow(info, tab?.Source ?? "") { Owner = this };
        dialog.ShowDialog();
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settingsService) { Owner = this };
        dialog.ShowDialog();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_sessionSaved)
        {
            return;
        }

        _sessionSaved = true;
        await _sessionService.SaveAsync(_viewModel.CaptureSession());
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.DisposeAllTabs();
        base.OnClosed(e);
    }
}
