using Xilium.CefGlue;
using Xilium.CefGlue.Avalonia;

namespace DemoBrowser.Services;

/// <summary>
/// The per-tab browser control. Thin subclass of <see cref="AvaloniaCefBrowser"/> that exposes the underlying
/// <see cref="CefBrowser"/> (needed for Stop, DevTools protocol access and the main-frame URL) — the equivalent
/// of <c>WebView2.CoreWebView2</c> in the Windows build.
/// </summary>
public sealed class TabBrowser : AvaloniaCefBrowser
{
    /// <summary>
    /// The <see cref="CefClient"/> the DevTools window runs on. Deliberately handler-free, see <see cref="ToggleDevTools"/>.
    /// </summary>
    private readonly CefClient _devToolsClient = new DevToolsClient();

    /// <summary>The CEF browser, or <c>null</c> until <c>BrowserInitialized</c> fired.</summary>
    public CefBrowser? Core => IsBrowserInitialized ? UnderlyingBrowser : null;

    /// <summary>
    /// Opens the DevTools window of this tab, or closes it when it is already open (F12 / Ctrl+Shift+I).
    /// CEF owns the window: it is a real, windowed browser next to the off-screen rendered tab.
    ///
    /// WHY not <c>AvaloniaCefBrowser.ShowDeveloperTools()</c>: CefGlue hands the tab's *own* <see cref="CefClient"/>
    /// to <c>ShowDevTools</c>, and its off-screen adapter's <c>OnBrowserClose</c> — unlike the windowed one — does not
    /// check <c>browser.IsPopup</c>. Closing the DevTools window therefore runs the adapter's <c>Cleanup</c> against
    /// the tab, nulling its browser and host and leaving a dead tab behind. A separate client keeps the DevTools
    /// browser off the tab's adapter entirely; with no handlers on it, CEF applies its default window behaviour.
    /// </summary>
    public void ToggleDevTools() => RunOnCefUiThread(() =>
    {
        var host = Core?.GetHost();
        if (host is null)
        {
            return;
        }

        try
        {
            if (host.HasDevTools)
            {
                host.CloseDevTools();
                return;
            }

            var windowInfo = CefWindowInfo.Create();
            if (CefRuntime.Platform == CefRuntimePlatform.Windows)
            {
                // Owned by the main window so it stays in front of it and goes away with it. macOS and Linux get
                // CEF's default DevTools window; SetAsPopup only carries Win32 window styles.
                windowInfo.SetAsPopup(host.GetWindowHandle(), "DevTools");
            }

            host.ShowDevTools(windowInfo, _devToolsClient, new CefBrowserSettings(), new CefPoint());
        }
        catch (Exception ex) when (ex is InvalidOperationException or CefRuntimeException or ObjectDisposedException)
        {
            // Browser already going away: nothing to inspect.
        }
    });

    /// <summary>Closes the DevTools window if one is open. Called before the tab's browser is disposed.</summary>
    public void CloseDevTools() => RunOnCefUiThread(() =>
    {
        try
        {
            var host = Core?.GetHost();
            if (host?.HasDevTools == true)
            {
                host.CloseDevTools();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or CefRuntimeException or ObjectDisposedException)
        {
        }
    });

    /// <summary>Runs <paramref name="action"/> on the CEF UI thread (required for DevTools/host calls).</summary>
    public static void RunOnCefUiThread(Action action)
    {
        if (CefRuntime.CurrentlyOn(CefThreadId.UI))
        {
            action();
        }
        else
        {
            CefRuntime.PostTask(CefThreadId.UI, new ActionTask(action));
        }
    }

    /// <summary>Carries a delegate onto the CEF UI thread, which only accepts <see cref="CefTask"/>.</summary>
    private sealed class ActionTask(Action action) : CefTask
    {
        protected override void Execute() => action();
    }

    /// <summary>A client without any handler: CEF drives the DevTools window with its own defaults.</summary>
    private sealed class DevToolsClient : CefClient;
}
