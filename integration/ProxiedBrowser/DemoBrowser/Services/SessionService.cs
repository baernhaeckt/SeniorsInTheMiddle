using System.IO;
using System.Text.Json;
using DemoBrowser.Models;

namespace DemoBrowser.Services;

/// <summary>Loads and saves the open-tab session (session.json).</summary>
public sealed class SessionService
{
    /// <summary>
    /// Returns the persisted session, or <c>null</c> if the file is missing, empty or corrupt.
    /// Never throws: a bad session file must not block startup.
    /// </summary>
    public async Task<SessionState?> LoadAsync()
    {
        try
        {
            if (!File.Exists(AppPaths.SessionFile))
            {
                return null;
            }

            await using var stream = File.OpenRead(AppPaths.SessionFile);
            var state = await JsonSerializer.DeserializeAsync(stream, AppJsonContext.Default.SessionState).ConfigureAwait(false);
            if (state is null)
            {
                return null;
            }

            state.TabUrls = state.TabUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            return state.TabUrls.Count == 0 ? null : state;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes the session. Failures are swallowed: losing the session must never prevent the app from closing.</summary>
    public async Task SaveAsync(SessionState state)
    {
        try
        {
            AppPaths.EnsureRootFolder();
            await using var stream = File.Create(AppPaths.SessionFile);
            await JsonSerializer.SerializeAsync(stream, state, AppJsonContext.Default.SessionState).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
