using System.Windows;
using DemoBrowser.Services;
using DemoBrowser.ViewModels;
using DemoBrowser.Views;

namespace DemoBrowser;

/// <summary>
/// Application bootstrap. Startup order is fixed:
/// 1. load settings, 2. verify the WebView2 Evergreen runtime, 3. download the proxy CA (non-fatal),
/// 4. create the single CoreWebView2Environment with the proxy switches (on a freshly wiped profile),
/// 5. open the start page. Nothing from a previous run is restored; the profile is wiped again on exit.
/// An animated splash screen is shown throughout (at least one second).
/// </summary>
public partial class App : Application
{
    private const string RuntimeDownloadUrl = "https://developer.microsoft.com/microsoft-edge/webview2/";
    private BrowserEnvironmentService? _environmentService;

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

    /// <summary>Earlier builds persisted open tabs to session.json; remove it so nothing is ever restored.</summary>
    private static void DeleteLegacySessionFile()
    {
        try
        {
            var legacy = System.IO.Path.Combine(AppPaths.RootFolder, "session.json");
            if (System.IO.File.Exists(legacy))
            {
                System.IO.File.Delete(legacy);
            }
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task StartAsync(SplashWindow splash)
    {
        // 1. Settings
        splash.SetStatus("Loading settings…");
        var settingsService = new SettingsService();
        var settings = await settingsService.LoadAsync();
        DeleteLegacySessionFile();

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
        _environmentService = new BrowserEnvironmentService();
        var environment = await _environmentService.GetOrCreateAsync(settings);

        // 5. Window + start page
        splash.SetStatus("Opening start page…");
        var viewModel = new MainViewModel(environment, certificateService, settingsService);
        if (caError is not null)
        {
            viewModel.WarningMessage = $"Proxy CA not loaded; HTTPS sites behind the proxy will show certificate errors. {caError}";
        }

        var window = new MainWindow(viewModel, settingsService);
        MainWindow = window;
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Hand over: fade the splash out (honouring its minimum display time), then reveal the main window.
        await splash.DismissAsync();
        window.Show();
        await window.OpenStartPageAsync();
    }

    /// <summary>
    /// Wipe the profile on exit too, so nothing lingers on disk between demo sessions. The WebView2 browser
    /// process keeps profile files locked until it has fully shut down, so wait for it (bounded) first.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _environmentService?.WaitForBrowserProcessExit(TimeSpan.FromSeconds(5));
        AppPaths.WipeBrowserData();
        base.OnExit(e);
    }
}
