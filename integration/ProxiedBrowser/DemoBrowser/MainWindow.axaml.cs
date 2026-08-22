using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DemoBrowser.Models;
using DemoBrowser.Services;
using DemoBrowser.ViewModels;
using DemoBrowser.Views;

namespace DemoBrowser;

/// <summary>
/// Main browser window with custom chrome (the tab strip doubles as the title bar).
/// The tab strip is a header-only ItemsControl; every tab's browser control is added to <c>WebViewHost</c>
/// exactly once when the tab is created and removed only when the tab closes.
/// </summary>
public partial class MainWindow : Window
{
    private static readonly TimeSpan BrowserShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settingsService;
    private DiagnosticsWindow? _diagnosticsWindow;
    private bool _closing;
    private bool _readyToClose;

    public MainWindow(MainViewModel viewModel, SettingsService settingsService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settingsService = settingsService;
        DataContext = viewModel;

        viewModel.Tabs.CollectionChanged += OnTabsChanged;
        viewModel.LastTabClosed += Close;
        viewModel.ActiveSourceChanged += OnActiveSourceChanged;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Ctrl on Windows/Linux, ⌘ on macOS (both registered so the build behaves the same everywhere).
        foreach (var modifier in new[] { KeyModifiers.Control, KeyModifiers.Meta })
        {
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.T, modifier), Command = viewModel.NewTabCommand });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.W, modifier), Command = new RelayCommand(() => viewModel.CloseTab(viewModel.ActiveTab)) });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.L, modifier), Command = new RelayCommand(FocusAddressBar) });
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.I, modifier | KeyModifiers.Shift), Command = viewModel.DevToolsCommand });
        }

        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F5), Command = viewModel.ReloadOrStopCommand });
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.F12), Command = viewModel.DevToolsCommand });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.D, KeyModifiers.Control | KeyModifiers.Shift),
            Command = new RelayCommand(ShowDiagnostics),
        });

        ApplyWindowState();
    }

    /// <summary>Opens the start page in a fresh tab. Called once by App after the window is shown.</summary>
    public Task OpenStartPageAsync() => _viewModel.OpenStartPageAsync();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            ApplyWindowState();
        }
    }

    /// <summary>Swaps the maximize/restore glyph (the OS handles the maximized geometry itself).</summary>
    private void ApplyWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "☐";
        ToolTip.SetTip(MaximizeButton, maximized ? "Restore" : "Maximize");
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    /// <summary>The title bar is the draggable caption area; a double-click toggles maximize like a native title bar.</summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            OnMaximizeClick(sender, e);
            return;
        }

        BeginMoveDrag(e);
    }

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

    private void OnAddressBarKeyDown(object? sender, KeyEventArgs e)
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

    private void OnAddressBarGotFocus(object? sender, GotFocusEventArgs e) => AddressBar.SelectAll();

    private void FocusAddressBar()
    {
        AddressBar.Focus();
        AddressBar.SelectAll();
    }

    /// <summary>Lock icon: shows the certificate / connection details of the active tab.</summary>
    private async void OnSecurityInfoClick(object? sender, RoutedEventArgs e)
    {
        var tab = _viewModel.ActiveTab;
        var info = tab?.SecurityInfo ?? new ConnectionSecurityInfo();
        var dialog = new CertificateInfoWindow(info, tab?.Source ?? "");
        await dialog.ShowDialog(this);
    }

    private async void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settingsService);
        await dialog.ShowDialog(this);
    }

    private void OnDiagnosticsClick(object? sender, RoutedEventArgs e) => ShowDiagnostics();

    /// <summary>
    /// Opens the proxy/certificate log. Modeless on purpose: the point is to reload a failing page and watch the
    /// decisions arrive, which a modal dialog would make impossible.
    /// </summary>
    private void ShowDiagnostics()
    {
        if (_diagnosticsWindow is not null)
        {
            _diagnosticsWindow.Activate();
            return;
        }

        _diagnosticsWindow = new DiagnosticsWindow(_viewModel.Diagnostics, _viewModel.CertificateService, _viewModel.Environment);
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show(this);
    }

    /// <summary>
    /// CEF destroys browsers asynchronously and needs the UI message loop for it. Closing is therefore done in
    /// two steps: dispose all tabs, keep the window (and loop) alive until every native browser is gone
    /// (bounded, like the WebView2 build's wait for its browser process), then really close.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_readyToClose)
        {
            e.Cancel = true;
            if (!_closing)
            {
                _closing = true;
                _ = CloseAfterBrowsersShutDownAsync();
            }
        }

        base.OnClosing(e);
    }

    private async Task CloseAfterBrowsersShutDownAsync()
    {
        var allClosed = _viewModel.DisposeAllTabs();
        await Task.WhenAny(allClosed, Task.Delay(BrowserShutdownTimeout));
        _readyToClose = true;
        Close();
    }
}
