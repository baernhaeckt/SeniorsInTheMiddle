using System.IO;

namespace DemoBrowser.Services;

/// <summary>Well-known file system locations used by the application.</summary>
public static class AppPaths
{
    public static string RootFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DemoBrowser");

    public static string UserDataFolder { get; } = Path.Combine(RootFolder, "WebView2");

    public static string SettingsFile { get; } = Path.Combine(RootFolder, "settings.json");

    public static void EnsureRootFolder() => Directory.CreateDirectory(RootFolder);

    /// <summary>
    /// Deletes the WebView2 profile (cookies, history, cache, local storage) so the browser starts fresh.
    /// Best effort: a locked file (e.g. a second instance still running) must not prevent startup.
    /// </summary>
    public static void WipeBrowserData()
    {
        // Chromium releases its last profile files a moment after the browser process exits, so retry briefly.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!Directory.Exists(UserDataFolder))
                {
                    return;
                }

                Directory.Delete(UserDataFolder, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
    }
}
