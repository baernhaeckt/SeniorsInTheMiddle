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

    /// <summary>Chromium (CEF) profile: cookies, cache, history, local storage. Wiped on every start and on exit.</summary>
    public static string UserDataFolder { get; } = Path.Combine(RootFolder, "CEF");

    public static string SettingsFile { get; } = Path.Combine(RootFolder, "settings.json");

    public static void EnsureRootFolder() => Directory.CreateDirectory(RootFolder);

    /// <summary>
    /// Deletes the CEF profile (cookies, history, cache, local storage) so the browser starts fresh.
    /// Best effort: a locked file (e.g. a second instance still running) must not prevent startup.
    /// </summary>
    public static void WipeBrowserData()
    {
        // Chromium releases its last profile files a moment after the browser engine shuts down, so retry briefly.
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

    /// <summary>
    /// Deletes the profile once this process has exited.
    ///
    /// WHY a detached helper: CEF runs the browser in-process, so Chromium's profile files (e.g. "Visited Links")
    /// stay memory-mapped by *this* process until it terminates — even after CefShutdown. An in-process delete on
    /// exit therefore always leaves parts of the profile behind. A tiny detached shell waits for our PID to
    /// disappear and then removes the folder, which keeps the guarantee that nothing lingers between demo
    /// sessions. Best effort: if the helper cannot be started, the profile is wiped on the next start anyway.
    /// </summary>
    public static void WipeBrowserDataAfterExit()
    {
        if (!Directory.Exists(UserDataFolder))
        {
            return;
        }

        var pid = System.Environment.ProcessId;
        var startInfo = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe",
                $"/c for /l %i in (1,1,150) do (tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul || (rmdir /s /q \"{UserDataFolder}\" & exit)) & timeout /t 1 >nul")
            : new System.Diagnostics.ProcessStartInfo("/bin/sh",
                $"-c \"while kill -0 {pid} 2>/dev/null; do sleep 0.2; done; rm -rf '{UserDataFolder}'\"");

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        try
        {
            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No shell available: the next start wipes the profile instead.
        }
    }
}
