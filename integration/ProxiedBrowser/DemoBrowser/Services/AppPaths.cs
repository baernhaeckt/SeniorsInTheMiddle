namespace DemoBrowser.Services;

/// <summary>Well-known file system locations used by the application.</summary>
public static class AppPaths
{
    /// <summary>
    /// macOS: <c>~/Library/Application Support/DemoBrowser</c>; Windows: <c>%LOCALAPPDATA%\DemoBrowser</c>
    /// (the Windows location is only relevant when this Avalonia build is run on Windows for development).
    /// </summary>
    public static string RootFolder { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "DemoBrowser")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DemoBrowser");

    /// <summary>
    /// Chromium (CEF) profile: cookies, cache, local storage. Kept between runs so logins and cached assets
    /// survive a restart; which tabs were open is deliberately never written anywhere.
    /// </summary>
    public static string UserDataFolder { get; } = Path.Combine(RootFolder, "CEF");

    public static string SettingsFile { get; } = Path.Combine(RootFolder, "settings.json");

    public static void EnsureRootFolder() => Directory.CreateDirectory(RootFolder);
}
