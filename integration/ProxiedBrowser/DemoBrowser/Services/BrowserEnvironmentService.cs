using System.Reflection;
using System.Runtime.CompilerServices;
using DemoBrowser.Models;
using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;
using Xilium.CefGlue.Common;

namespace DemoBrowser.Services;

/// <summary>
/// Describes the single, process-wide Chromium (CEF) engine instance every tab shares.
/// The CEF equivalent of WebView2's <c>CoreWebView2Environment</c>.
/// </summary>
public sealed class BrowserEnvironment
{
    internal BrowserEnvironment(AppSettings settings, string browserArguments, string chromeVersion)
    {
        Settings = settings;
        BrowserArguments = browserArguments;
        ChromeVersion = chromeVersion;
    }

    /// <summary>The settings the engine was started with (a snapshot; later edits need a restart).</summary>
    public AppSettings Settings { get; }

    /// <summary>The Chromium switches passed to the engine, for display/diagnostics.</summary>
    public string BrowserArguments { get; }

    public string ChromeVersion { get; }
}

/// <summary>
/// Owns the single CEF runtime shared by every tab.
///
/// WHY one environment: all tabs share the same cache path, and therefore the same cookies,
/// session storage and cache, across tabs. The folder is kept between runs (cookies, cache and storage
/// survive a restart); which tabs were open is never persisted.
///
/// WHY the proxy is an environment-time argument: Chromium reads <c>--proxy-server</c> (and the SPKI pins) only
/// from the command line of the browser process, which CEF configures when the runtime is initialised (once per
/// process). There is no API to change either on a live runtime, so proxy/CA changes are applied by restarting
/// the process in flight (<see cref="App.RestartInFlightAsync"/>), which reopens the same tabs.
/// </summary>
public sealed class BrowserEnvironmentService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<BrowserEnvironment>? _creation;

    public BrowserEnvironment? Environment { get; private set; }

    /// <summary>
    /// With CEF the browser process is this process (only renderers/GPU run in the helper executable),
    /// so the PID is simply our own. Kept for parity with the WebView2 build.
    /// </summary>
    public static int BrowserProcessId => System.Environment.ProcessId;

    /// <summary>
    /// Returns the bundled Chromium version, or <c>null</c> if the CEF runtime (libcef + helper process)
    /// cannot be loaded from the application folder.
    /// </summary>
    public static string? GetInstalledRuntimeVersion()
    {
        try
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "CefGlueBrowserProcess",
                "Xilium.CefGlue.BrowserProcess" + (OperatingSystem.IsWindows() ? ".exe" : ""));
            if (!File.Exists(helper))
            {
                return null;
            }

            CefRuntime.Load();
            return CefRuntime.ChromeVersion;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the Chromium switches for the proxy. Values are intentionally NOT quoted: they are
    /// passed as individual switches and literal quotes would be passed through to Chromium verbatim.
    /// </summary>
    /// <param name="proxyTlsPins">
    /// SPKI pins (base64 of SHA-256 over the DER SubjectPublicKeyInfo) of certificates Chromium should accept
    /// without validating them itself, passed as <c>--ignore-certificate-errors-spki-list</c>.
    ///
    /// WHY this switch is here: with <c>ProxyScheme = https</c> Chromium speaks TLS to the *proxy*, whose
    /// certificate is signed by the proxy's own CA. A bad proxy certificate never reaches
    /// <c>OnCertificateError</c> — Chromium fails the connection outright and every page stays blank — so this is
    /// the only place the decision can be made. The list holds exactly the certificate
    /// <see cref="CertificateService.ProbeProxyTlsPinAsync"/> already validated against the proxy CA in-process;
    /// site certificates are not covered by it and still go through the in-app trust decision.
    /// </param>
    public static KeyValuePair<string, string>[] BuildBrowserSwitches(
        AppSettings settings,
        IReadOnlyCollection<string>? proxyTlsPins = null)
    {
        if (!settings.UseProxy)
        {
            // Direct connection. Without --no-proxy-server Chromium would silently fall back to the Windows/macOS
            // system proxy configuration, which is not what "proxy off" means for this demo.
            return [new("no-proxy-server", "")];
        }

        var scheme = string.Equals(settings.ProxyScheme, "https", StringComparison.OrdinalIgnoreCase) ? "https" : "http";
        var switches = new List<KeyValuePair<string, string>>
        {
            new("proxy-server", $"{scheme}://{settings.ProxyHost.Trim()}:{settings.ProxyPort}"),
        };
        if (!string.IsNullOrWhiteSpace(settings.ProxyBypassList))
        {
            switches.Add(new("proxy-bypass-list", settings.ProxyBypassList.Trim()));
        }

        var pins = (proxyTlsPins ?? [])
            .Where(pin => !string.IsNullOrWhiteSpace(pin))
            .Select(pin => pin.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (pins.Length > 0)
        {
            switches.Add(new("ignore-certificate-errors-spki-list", string.Join(',', pins)));
        }

        return [.. switches];
    }

    /// <summary>Same switches as a single command-line string (what the WebView2 build passes verbatim).</summary>
    public static string BuildBrowserArguments(AppSettings settings, IReadOnlyCollection<string>? proxyTlsPins = null) =>
        string.Join(' ', BuildBrowserSwitches(settings, proxyTlsPins)
            .Select(s => s.Value.Length == 0 ? $"--{s.Key}" : $"--{s.Key}={s.Value}"));

    /// <summary>Creates the environment exactly once; concurrent and repeated callers share the same cached task.</summary>
    public async Task<BrowserEnvironment> GetOrCreateAsync(AppSettings settings, IReadOnlyCollection<string>? proxyTlsPins = null)
    {
        if (_creation is null)
        {
            await _gate.WaitAsync().ConfigureAwait(true);
            try
            {
                _creation ??= CreateAsync(settings, proxyTlsPins);
            }
            finally
            {
                _gate.Release();
            }
        }

        return await _creation.ConfigureAwait(true);
    }

    /// <summary>
    /// Shuts the engine down so its profile files are unlocked. Must run on the main thread after the UI loop has
    /// ended and after every browser is closed (<c>MainWindow</c> waits for that before it lets itself close).
    /// CefGlue's own ProcessExit hook calls CefShutdown again later; CEF ignores the second call.
    /// </summary>
    public void ShutdownBrowserEngine()
    {
        if (Environment is null || !CefRuntime.IsInitialized)
        {
            return;
        }

        try
        {
            CefRuntime.Shutdown();
        }
        catch (Exception ex) when (ex is InvalidOperationException or CefRuntimeException)
        {
            // Already shut down: nothing to wait for.
        }
    }

    private Task<BrowserEnvironment> CreateAsync(AppSettings settings, IReadOnlyCollection<string>? proxyTlsPins)
    {
        // The profile persists between runs: cookies (session cookies included), cache and local storage are
        // picked up again. Open tabs are not part of the profile — CEF has no session restore and the app keeps
        // none of its own.
        Directory.CreateDirectory(AppPaths.UserDataFolder);

        var cefSettings = new CefSettings
        {
            RootCachePath = AppPaths.UserDataFolder,
            CachePath = AppPaths.UserDataFolder,
            PersistSessionCookies = true,
            LogSeverity = CefLogSeverity.Warning,
            LogFile = Path.Combine(AppPaths.RootFolder, "cef.log"),
            WindowlessRenderingEnabled = false,
        };

        // CefGlue defers the real CefInitialize until the first browser control is created. Force it now so
        // a broken runtime surfaces here (on the splash, like the WebView2 build) and not inside the first tab.
        CefRuntimeLoader.Initialize(cefSettings, BuildBrowserSwitches(settings, proxyTlsPins));
        ForceRuntimeLoad();

        var environment = new BrowserEnvironment(settings.Clone(), BuildBrowserArguments(settings, proxyTlsPins), CefRuntime.ChromeVersion);
        Environment = environment;
        return Task.FromResult(environment);
    }

    private static void ForceRuntimeLoad()
    {
        if (OperatingSystem.IsMacOS())
        {
            // On macOS CefGlue wires its Avalonia message-pump handler in AvaloniaCefBrowser's type initialiser.
            RuntimeHelpers.RunClassConstructor(typeof(AvaloniaCefBrowser).TypeHandle);
        }

        if (CefRuntimeLoader.IsLoaded)
        {
            return;
        }

        // Windows/Linux: CefGlue loads lazily from the browser constructor via an internal overload.
        var load = typeof(CefRuntimeLoader).GetMethod("Load", BindingFlags.Static | BindingFlags.NonPublic);
        if (load is null)
        {
            return;
        }

        try
        {
            load.Invoke(null, [null]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }
}
