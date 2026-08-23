using Avalonia;

namespace DemoBrowser;

/// <summary>Process entry point for the demo browser.</summary>
public static class Program
{
    /// <summary>
    /// Avalonia entry point. CEF is initialised later, by <see cref="Services.BrowserEnvironmentService"/>, once the
    /// Avalonia platform (and on macOS the NSApplication) is up.
    /// </summary>
    [STAThread]
    public static int Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
