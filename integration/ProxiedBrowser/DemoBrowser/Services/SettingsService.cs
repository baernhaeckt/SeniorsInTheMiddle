using System.IO;
using System.Text.Json;
using DemoBrowser.Models;

namespace DemoBrowser.Services;

/// <summary>Loads and saves <see cref="AppSettings"/> from settings.json.</summary>
public sealed class SettingsService
{
    public AppSettings Current { get; private set; } = new();

    /// <summary>Loads settings; creates the file with defaults if it is missing. A corrupt file falls back to defaults.</summary>
    public async Task<AppSettings> LoadAsync()
    {
        AppPaths.EnsureRootFolder();

        if (!File.Exists(AppPaths.SettingsFile))
        {
            Current = new AppSettings();
            await SaveAsync(Current).ConfigureAwait(false);
            return Current;
        }

        try
        {
            await using var stream = File.OpenRead(AppPaths.SettingsFile);
            var loaded = await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.AppSettings).ConfigureAwait(false);
            Current = loaded ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Current = new AppSettings();
        }

        return Current;
    }

    public async Task SaveAsync(AppSettings settings)
    {
        AppPaths.EnsureRootFolder();
        await using (var stream = File.Create(AppPaths.SettingsFile))
        {
            await JsonSerializer.SerializeAsync(stream, settings, AppJsonContext.Default.AppSettings).ConfigureAwait(false);
        }

        Current = settings;
    }
}
