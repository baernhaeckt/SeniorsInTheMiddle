using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DemoBrowser.Models;
using DemoBrowser.Services;

namespace DemoBrowser.Views;

/// <summary>Edits settings.json. Proxy changes only apply after restart (see <see cref="BrowserEnvironmentService"/>).</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;
    private bool _populating;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        Populate(settingsService.Current);
    }

    private void Populate(AppSettings s)
    {
        _populating = true;
        try
        {
            UseProxyBox.IsChecked = s.UseProxy;
            SchemeBox.SelectedIndex = string.Equals(s.ProxyScheme, "https", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            HostBox.Text = s.ProxyHost;
            PortBox.Text = s.ProxyPort.ToString();
            BypassBox.Text = s.ProxyBypassList;
            CaUrlBox.Text = s.CaCertUrl;
            StartPageBox.Text = s.StartPage;
        }
        finally
        {
            _populating = false;
        }
    }

    /// <summary>
    /// The proxy listens on 3128 (http) and 3127 (https). When the scheme changes and the port still holds the
    /// other scheme's default, swap it so the user does not have to remember the pair.
    /// </summary>
    private void OnSchemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_populating || !int.TryParse((PortBox.Text ?? "").Trim(), out var port))
        {
            return;
        }

        var https = SchemeBox.SelectedIndex == 1;
        if (https && port == AppSettings.DefaultHttpProxyPort)
        {
            PortBox.Text = AppSettings.DefaultHttpsProxyPort.ToString();
        }
        else if (!https && port == AppSettings.DefaultHttpsProxyPort)
        {
            PortBox.Text = AppSettings.DefaultHttpProxyPort.ToString();
        }
    }

    /// <summary>Fills the form with the built-in defaults; nothing is written until Save is clicked.</summary>
    private void OnResetClick(object? sender, RoutedEventArgs e) => Populate(new AppSettings());

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // With the proxy switched off none of the proxy fields are used, so they are kept as-is but not validated.
        var useProxy = UseProxyBox.IsChecked == true;

        var host = (HostBox.Text ?? "").Trim();
        if (useProxy && (string.IsNullOrWhiteSpace(host) || host.Contains(' ')))
        {
            await MessageDialog.ShowAsync(this, "Proxy host must not be empty or contain spaces.", "Invalid settings", MessageDialogIcon.Warning);
            HostBox.Focus();
            return;
        }

        if (!int.TryParse((PortBox.Text ?? "").Trim(), out var port) || port is < 1 or > 65535)
        {
            if (useProxy)
            {
                await MessageDialog.ShowAsync(this, "Proxy port must be a number between 1 and 65535.", "Invalid settings", MessageDialogIcon.Warning);
                PortBox.Focus();
                return;
            }

            port = AppSettings.DefaultHttpProxyPort;
        }

        var caUrl = (CaUrlBox.Text ?? "").Trim();
        if (useProxy
            && (!Uri.TryCreate(caUrl, UriKind.Absolute, out var caUri)
                || (caUri.Scheme != Uri.UriSchemeHttps && caUri.Scheme != Uri.UriSchemeHttp)))
        {
            await MessageDialog.ShowAsync(this, "CA certificate URL must be an absolute http:// or https:// URL.", "Invalid settings", MessageDialogIcon.Warning);
            CaUrlBox.Focus();
            return;
        }

        var startPage = (StartPageBox.Text ?? "").Trim();
        if (!Uri.TryCreate(startPage, UriKind.Absolute, out _))
        {
            await MessageDialog.ShowAsync(this, "Start page must be an absolute URL.", "Invalid settings", MessageDialogIcon.Warning);
            StartPageBox.Focus();
            return;
        }

        var previous = _settingsService.Current;
        var updated = new AppSettings
        {
            UseProxy = useProxy,
            ProxyScheme = (SchemeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "http",
            ProxyHost = host,
            ProxyPort = port,
            ProxyBypassList = (BypassBox.Text ?? "").Trim(),
            CaCertUrl = caUrl,
            StartPage = startPage,
        };

        var proxyChanged = previous.UseProxy != updated.UseProxy
                           || previous.ProxyScheme != updated.ProxyScheme
                           || previous.ProxyHost != updated.ProxyHost
                           || previous.ProxyPort != updated.ProxyPort
                           || previous.ProxyBypassList != updated.ProxyBypassList
                           || previous.CaCertUrl != updated.CaCertUrl;

        try
        {
            await _settingsService.SaveAsync(updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await MessageDialog.ShowAsync(this, $"Could not write settings: {ex.Message}", "Error", MessageDialogIcon.Error);
            return;
        }

        if (proxyChanged)
        {
            await MessageDialog.ShowAsync(
                this,
                "Settings saved.\n\nProxy and CA changes take effect only after restarting the application: " +
                "Chromium reads the proxy exclusively when the browser engine is initialised and it cannot be changed on a running engine.",
                "Restart required",
                MessageDialogIcon.Information);
        }

        Close(true);
    }
}
