using System.Windows;
using System.Windows.Controls;
using DemoBrowser.Models;
using DemoBrowser.Services;

namespace DemoBrowser.Views;

/// <summary>Edits settings.json. Proxy changes only apply after restart (see <see cref="BrowserEnvironmentService"/>).</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settingsService;

    public SettingsWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;
        Populate(settingsService.Current);
    }

    private void Populate(AppSettings s)
    {
        SchemeBox.SelectedIndex = string.Equals(s.ProxyScheme, "https", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        HostBox.Text = s.ProxyHost;
        PortBox.Text = s.ProxyPort.ToString();
        BypassBox.Text = s.ProxyBypassList;
        CaUrlBox.Text = s.CaCertUrl;
        StartPageBox.Text = s.StartPage;
    }

    /// <summary>
    /// The proxy listens on 3128 (http) and 3127 (https). When the scheme changes and the port still holds the
    /// other scheme's default, swap it so the user does not have to remember the pair.
    /// </summary>
    private void OnSchemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !int.TryParse(PortBox.Text.Trim(), out var port))
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
    private void OnResetClick(object sender, RoutedEventArgs e) => Populate(new AppSettings());

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host) || host.Contains(' '))
        {
            MessageBox.Show(this, "Proxy host must not be empty or contain spaces.", "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            HostBox.Focus();
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Proxy port must be a number between 1 and 65535.", "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            PortBox.Focus();
            return;
        }

        var caUrl = CaUrlBox.Text.Trim();
        if (!Uri.TryCreate(caUrl, UriKind.Absolute, out var caUri)
            || (caUri.Scheme != Uri.UriSchemeHttps && caUri.Scheme != Uri.UriSchemeHttp))
        {
            MessageBox.Show(this, "CA certificate URL must be an absolute http:// or https:// URL.", "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            CaUrlBox.Focus();
            return;
        }

        var startPage = StartPageBox.Text.Trim();
        if (!Uri.TryCreate(startPage, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Start page must be an absolute URL.", "Invalid settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            StartPageBox.Focus();
            return;
        }

        var previous = _settingsService.Current;
        var updated = new AppSettings
        {
            ProxyScheme = (SchemeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "http",
            ProxyHost = host,
            ProxyPort = port,
            ProxyBypassList = BypassBox.Text.Trim(),
            CaCertUrl = caUrl,
            StartPage = startPage,
        };

        var proxyChanged = previous.ProxyScheme != updated.ProxyScheme
                           || previous.ProxyHost != updated.ProxyHost
                           || previous.ProxyPort != updated.ProxyPort
                           || previous.ProxyBypassList != updated.ProxyBypassList
                           || previous.CaCertUrl != updated.CaCertUrl;

        try
        {
            await _settingsService.SaveAsync(updated);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Could not write settings: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (proxyChanged)
        {
            MessageBox.Show(
                this,
                "Settings saved.\n\nProxy and CA changes take effect only after restarting the application: " +
                "WebView2 reads the proxy exclusively when its browser environment is created and it cannot be changed on a running environment.",
                "Restart required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        DialogResult = true;
        Close();
    }
}
