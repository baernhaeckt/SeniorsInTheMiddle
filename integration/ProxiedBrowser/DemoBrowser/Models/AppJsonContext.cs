using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoBrowser.Models;

/// <summary>Source-generated JSON context for the settings and session files (no reflection, trim-safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SessionState))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
