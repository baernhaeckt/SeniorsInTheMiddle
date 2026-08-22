using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DemoBrowser.Models;
using DemoBrowser.Services;
using DemoBrowser.ViewModels;
using DemoBrowser.Views;

namespace DemoBrowser;

/// <summary>
/// Application bootstrap. Startup order is fixed:
/// 1. load settings, 2. verify the bundled Chromium (CEF) runtime, 3. download the proxy CA (non-fatal),
/// 4. initialise the single CEF runtime with the proxy switches (on the persistent profile: cookies, cache and
///    storage are kept between runs),
/// 5. open the start page. Which tabs were open is never stored; only an in-flight restart
///    (<see cref="RestartInFlightAsync"/>) hands the current tabs to its successor, via the command line.
/// An animated splash screen is shown throughout (at least one second).
/// </summary>
public partial class App : Application
{
    private const string RuntimeHelp = "Rebuild the application with publish.sh (see README.md) so that the CEF runtime " +
                                       "and the CefGlueBrowserProcess helper are bundled next to the executable.";

    private static readonly TimeSpan PredecessorExitTimeout = TimeSpan.FromSeconds(15);

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

    /// <summary>
    /// An in-flight restart hands its tabs to the successor on the command line; the successor must not touch the
    /// profile before its predecessor has released it (Chromium refuses a cache_path another process still uses).
    /// </summary>
    private static async Task WaitForPredecessorAsync(RestartState state, SplashWindow splash)
    {
        if (state.PreviousProcessId <= 0 || state.PreviousProcessId == Environment.ProcessId)
        {
            return;
        }

        Process predecessor;
        try
        {
            predecessor = Process.GetProcessById(state.PreviousProcessId);
        }
        catch (ArgumentException)
        {
            return; // already gone
        }

        splash.SetStatus("Restarting browser engine…");
        using var timeout = new CancellationTokenSource(PredecessorExitTimeout);
        try
        {
            await predecessor.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Carry on; the engine start reports the locked profile if it is still held.
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            predecessor.Dispose();
        }
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        // 0. Restarted in flight? Then wait until the previous instance has let go of the profile.
        var restart = RestartState.TryParse(desktop.Args);
        if (restart is not null)
        {
            await WaitForPredecessorAsync(restart, splash);
        }

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

        // 3b. The certificates Chromium will never ask about: the proxy's own, and the ones it mints.
        //
        // WHY before the engine starts: OnCertificateError is offered for main-frame navigations only. A
        // subresource on another origin — a script, an API call, an image — is denied outright, and so is a bad
        // certificate on the connection to the proxy itself. Neither reaches
        // CertificateService.HandleServerCertificateError, so neither can be repaired once the engine is running:
        // the document would load and everything it pulls from another host would fail. Both are therefore
        // validated here against the CA that was just downloaded and handed over as command-line pins.
        IReadOnlyList<string> proxyTlsPins = [];
        string? proxyTlsError = null;
        if (settings.UseProxy)
        {
            splash.SetStatus("Checking the proxy…");
            var endpoint = new CertificateService.ProxyEndpoint(
                settings.ProxyHost.Trim(),
                settings.ProxyPort,
                string.Equals(settings.ProxyScheme, "https", StringComparison.OrdinalIgnoreCase));

            // The start page is the natural probe target: it is the first thing the browser opens anyway, so a
            // proxy that cannot serve it is a problem the user is about to meet regardless.
            var probeHost = Uri.TryCreate(settings.StartPage, UriKind.Absolute, out var start)
                && start.Scheme == Uri.UriSchemeHttps ? start.Host : null;

            proxyTlsPins = await certificateService.CollectProxyPinsAsync(endpoint, probeHost);
            if (proxyTlsPins.Count == 0)
            {
                proxyTlsError = $"Nothing could be verified against the proxy CA at {endpoint}. Pages behind the "
                    + "proxy will show certificate errors. Proxy diagnostics (Ctrl+Shift+D) has the reason.";
            }
        }

        // 4. Single shared environment
        splash.SetStatus("Starting browser engine…");
        _environmentService = new BrowserEnvironmentService();
        var environment = await _environmentService.GetOrCreateAsync(settings, proxyTlsPins);
        _diagnostics.Info("Proxy", "Chromium switches (final): "
            + BrowserEnvironmentService.BuildBrowserArguments(settings, proxyTlsPins));

        // 5. Window + start page
        splash.SetStatus("Opening start page…");
        var viewModel = new MainViewModel(environment, certificateService, settingsService, _diagnostics);
        if (caError is not null)
        {
            viewModel.WarningMessage = $"Proxy CA not loaded; HTTPS sites behind the proxy will show certificate errors. {caError}";
        }
        else if (proxyTlsError is not null)
        {
            viewModel.WarningMessage = proxyTlsError;
        }

        var window = new MainWindow(viewModel, settingsService);
        if (restart is not null)
        {
            window.ApplyRestartState(restart);
        }

        viewModel.RestartRequested += reason => _ = RestartInFlightAsync(window, reason);
        desktop.MainWindow = window;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

        // Hand over: fade the splash out (honouring its minimum display time), then reveal the main window.
        await splash.DismissAsync();
        window.Show();
        if (restart is not null)
        {
            await viewModel.OpenTabsAsync(restart.TabUrls, restart.ActiveTabIndex);
        }
        else
        {
            await window.OpenStartPageAsync();
        }
    }

    /// <summary>
    /// Re-initialises the browser engine without the user having to close and reopen the app.
    ///
    /// WHY a new process: the proxy and the SPKI pins for the proxy CA are command-line switches of the browser
    /// process, and CEF initialises the runtime exactly once per process — there is no "reload the control with
    /// new settings". So a successor process is started with the current tabs and window geometry as arguments
    /// (in memory only, nothing is written to disk), it shows the splash and waits for this instance to release
    /// the profile, and this instance closes. From the outside it looks like a reload of the window.
    /// </summary>
    private async Task RestartInFlightAsync(MainWindow window, string reason)
    {
        var state = window.CaptureRestartState();
        var executable = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executable))
        {
            _diagnostics.Error("Restart", "Cannot restart: the executable path is unknown");
            return;
        }

        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in state.ToArguments())
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var successor = Process.Start(startInfo);
            if (successor is null)
            {
                _diagnostics.Error("Restart", "Cannot restart: the successor process did not start");
                return;
            }

            _diagnostics.Info("Restart", $"Successor process {successor.Id} started ({reason}); closing this instance");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _diagnostics.Error("Restart", $"Cannot restart: {ex.Message}", ex.ToString());
            await MessageDialog.ShowAsync(window,
                $"The browser engine could not be restarted ({ex.Message}). Please close and reopen the application.",
                "Restart failed", MessageDialogIcon.Warning);
            return;
        }

        window.CloseForRestart();
    }

    /// <summary>
    /// Shut the engine down cleanly so the profile (cookies, cache, storage) is flushed and unlocked for the next
    /// run. This runs after the Avalonia loop has finished and after every browser is closed
    /// (<see cref="MainWindow"/> waits for that before it lets itself close), which is what CefShutdown requires.
    /// </summary>
    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e) =>
        _environmentService?.ShutdownBrowserEngine();
}
