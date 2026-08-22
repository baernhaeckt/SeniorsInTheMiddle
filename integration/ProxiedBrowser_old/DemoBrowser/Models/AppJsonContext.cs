using System.Text.Json;
using System.Text.Json.Serialization;

namespace DemoBrowser.Models;

/// <summary>Source-generated JSON context for the settings file (no reflection, trim-safe).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
