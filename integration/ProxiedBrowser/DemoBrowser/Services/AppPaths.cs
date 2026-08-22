using System.IO;

namespace DemoBrowser.Services;

/// <summary>Well-known file system locations used by the application.</summary>
public static class AppPaths
{
    public static string RootFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DemoBrowser");

    public static string UserDataFolder { get; } = Path.Combine(RootFolder, "WebView2");

    public static string SettingsFile { get; } = Path.Combine(RootFolder, "settings.json");

    public static string SessionFile { get; } = Path.Combine(RootFolder, "session.json");

    public static void EnsureRootFolder() => Directory.CreateDirectory(RootFolder);
}
