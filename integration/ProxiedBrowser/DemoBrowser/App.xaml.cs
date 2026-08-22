using System.Windows;
using DemoBrowser.Services;
using DemoBrowser.ViewModels;
using DemoBrowser.Views;

namespace DemoBrowser;

/// <summary>
/// Application bootstrap. Startup order is fixed:
/// 1. load settings, 2. verify the WebView2 Evergreen runtime, 3. download the proxy CA (non-fatal),
/// 4. create the single CoreWebView2Environment with the proxy switches, 5. restore the session.
/// An animated splash screen is shown throughout (at least one second).
/// </summary>
public partial class App : Application
{
    private const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var splash = new SplashWindow();
        splash.Show();

        try
        {
            await StartAsync(splash);
        }
        catch (Exception ex)
        {
            splash.Close();
            MessageBox.Show(
                $"Demo Browser could not start.\n\n{ex.GetType().Name}: {ex.Message}",
                "Demo Browser",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync(SplashWindow splash)
    {
        // 1. Settings
        splash.SetStatus("Loading settings…");
        var settingsService = new SettingsService();
        var settings = await settingsService.LoadAsync();

        // 2. Runtime check
        splash.SetStatus("Checking WebView2 runtime…");
        if (BrowserEnvironmentService.GetInstalledRuntimeVersion() is null)
        {
            splash.Close();
            MessageBox.Show(
                "The Microsoft Edge WebView2 Runtime is not installed.\n\n" +
                $"Please install the Evergreen runtime from:\n{RuntimeDownloadUrl}",
                "WebView2 Runtime missing",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        // 3. Proxy CA (non-fatal)
        splash.SetStatus("Fetching proxy certificate…");
        var certificateService = new CertificateService();
        var caError = await certificateService.DownloadAsync(settings.CaCertUrl);

        // 4. Single shared environment
        splash.SetStatus("Starting browser engine…");
        var environmentService = new BrowserEnvironmentService();
        var environment = await environmentService.GetOrCreateAsync(settings);

        // 5. Window + session
        splash.SetStatus("Restoring session…");
        var sessionService = new SessionService();
        var viewModel = new MainViewModel(environment, certificateService, settingsService);
        if (caError is not null)
        {
            viewModel.WarningMessage = $"Proxy CA not loaded; HTTPS sites behind the proxy will show certificate errors. {caError}";
        }

        var window = new MainWindow(viewModel, settingsService, sessionService);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        var session = await sessionService.LoadAsync();

        // Hand over: fade the splash out (honouring its minimum display time), then reveal the main window.
        await splash.DismissAsync();
        window.Show();
        await window.RestoreSessionAsync(session);
    }
}
