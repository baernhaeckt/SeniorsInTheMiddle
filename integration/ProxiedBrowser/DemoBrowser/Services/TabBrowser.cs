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
    /// <summary>The CEF browser, or <c>null</c> until <c>BrowserInitialized</c> fired.</summary>
    public CefBrowser? Core => IsBrowserInitialized ? UnderlyingBrowser : null;

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

    private sealed class ActionTask(Action action) : CefTask
    {
        protected override void Execute() => action();
    }
}
