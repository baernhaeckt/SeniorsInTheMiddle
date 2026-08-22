using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DemoBrowser.Services;
using DemoBrowser.ViewModels;
using DemoBrowser.Views;

namespace DemoBrowser;

/// <summary>
/// Application bootstrap. Startup order is fixed:
/// 1. load settings, 2. verify the bundled Chromium (CEF) runtime, 3. download the proxy CA (non-fatal),
/// 4. initialise the single CEF runtime with the proxy switches (on a freshly wiped profile),
/// 5. open the start page. Nothing from a previous run is restored; the profile is wiped again on exit.
/// An animated splash screen is shown throughout (at least one second).
/// </summary>
public partial class App : Application
{
    private const string RuntimeHelp = "Rebuild the application with publish.sh (see README.md) so that the CEF runtime " +
                                       "and the CefGlueBrowserProcess helper are bundled next to the executable.";

    private readonly ProxyDiagnostics _diagnostics = new();
    private BrowserEnvironmentService? _environmentService;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += OnExit;
            // Run once the lifetime's message loop is up (the equivalent of WPF's OnStartup).
            Dispatcher.UIThread.Post(() => _ = OnStartupAsync(desktop));
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task OnStartupAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var splash = new SplashWindow();
        splash.Show();

        try
        {
            await StartAsync(desktop, splash);
        }
        catch (Exception ex)
        {
            splash.Close();
            await MessageDialog.ShowAsync(
                null,
                $"Demo Browser could not start.\n\n{ex.GetType().Name}: {ex.Message}",
                "Demo Browser",
                MessageDialogIcon.Error);
            desktop.Shutdown(1);
        }
    }

    /// <summary>Earlier builds persisted open tabs to session.json; remove it so nothing is ever restored.</summary>
    private static void DeleteLegacySessionFile()
    {
        try
        {
            var legacy = Path.Combine(AppPaths.RootFolder, "session.json");
            if (File.Exists(legacy))
            {
                File.Delete(legacy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        // 1. Settings
        splash.SetStatus("Loading settings…");
        var settingsService = new SettingsService();
        var settings = await settingsService.LoadAsync();
        DeleteLegacySessionFile();

        // 2. Runtime check
        splash.SetStatus("Checking Chromium runtime…");
        if (BrowserEnvironmentService.GetInstalledRuntimeVersion() is null)
        {
            splash.Close();
            await MessageDialog.ShowAsync(
                null,
                "The Chromium Embedded Framework runtime is missing or could not be loaded.\n\n" + RuntimeHelp,
                "Chromium runtime missing",
                MessageDialogIcon.Error);
            desktop.Shutdown(2);
            return;
        }

        // 3. Proxy CA (non-fatal; nothing to fetch when the proxy is switched off)
        _diagnostics.Info("Proxy", settings.UseProxy
            ? $"Proxy enabled: {settings.ProxyScheme}://{settings.ProxyHost}:{settings.ProxyPort}"
            : "Proxy disabled (UseProxy = false): connecting directly, no CA is loaded");
        _diagnostics.Info("Proxy", "Chromium switches: " + BrowserEnvironmentService.BuildBrowserArguments(settings));

        var certificateService = new CertificateService(_diagnostics);
        string? caError = null;
        if (settings.UseProxy)
        {
            splash.SetStatus("Fetching proxy certificate…");
            caError = await certificateService.DownloadAsync(settings.CaCertUrl);
        }

        // 4. Single shared environment
        splash.SetStatus("Starting browser engine…");
        _environmentService = new BrowserEnvironmentService();
        var environment = await _environmentService.GetOrCreateAsync(settings);

        // 5. Window + start page
        splash.SetStatus("Opening start page…");
        var viewModel = new MainViewModel(environment, certificateService, settingsService, _diagnostics);
        if (caError is not null)
        {
            viewModel.WarningMessage = $"Proxy CA not loaded; HTTPS sites behind the proxy will show certificate errors. {caError}";
        }

        var window = new MainWindow(viewModel, settingsService);
        desktop.MainWindow = window;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Hand over: fade the splash out (honouring its minimum display time), then reveal the main window.
        await splash.DismissAsync();
        window.Show();
        await window.OpenStartPageAsync();
    }

    /// <summary>
    /// Wipe the profile on exit too, so nothing lingers on disk between demo sessions. Chromium keeps profile
    /// files locked until the engine has fully shut down, so shut it down first. This runs after the Avalonia
    /// loop has finished and after every browser is closed (<see cref="MainWindow"/> waits for that before it
    /// lets itself close), which is what CefShutdown requires.
    /// </summary>
    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        _environmentService?.ShutdownBrowserEngine();
        AppPaths.WipeBrowserData();

        // CEF hosts the browser in-process, so parts of the profile stay memory-mapped until this process is gone;
        // a detached helper finishes the job right after exit.
        AppPaths.WipeBrowserDataAfterExit();
    }
}
