namespace DemoBrowser.Models;

/// <summary>Persisted session (%LOCALAPPDATA%\DemoBrowser\session.json): the open tabs' URLs.</summary>
public sealed class SessionState
{
    public List<string> TabUrls { get; set; } = [];

    public int ActiveTabIndex { get; set; }
}
