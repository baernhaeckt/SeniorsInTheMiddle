using System.Text.Json;
using System.Text.Json.Serialization;

namespace SeniorsInTheMiddle.Proxy.Services.Pii;

/// <summary>
/// Result of the python <c>analyze</c> method (services/pii_service). The python side
/// emits snake_case, see <see cref="PiiJson.Options"/>. When nothing was found the service
/// returns an empty object, which deserializes to an instance with
/// <see cref="DetectionCount"/> 0 -- see <see cref="Empty"/>.
/// </summary>
public sealed record PiiAnalyzeResult
{
    public static readonly PiiAnalyzeResult Empty = new();

    public IReadOnlyList<PiiDetection> DetectionResults { get; init; } = [];

    public int DetectionCount { get; init; }

    public double RiskScoreMean { get; init; }

    public double RiskScoreMedian { get; init; }

    public IReadOnlyList<string> DetectedPiiTypes { get; init; } = [];

    public IReadOnlyDictionary<string, int> DetectedPiiTypeFrequencies { get; init; } = new Dictionary<string, int>();

    [JsonIgnore]
    public bool HasDetections => DetectionCount > 0 || DetectionResults.Count > 0;
}

/// <summary>One entity found in the text. Positions are character offsets, end exclusive.</summary>
public sealed record PiiDetection
{
    public string InformationType { get; init; } = string.Empty;

    /// <summary>The <c>PiiTypes</c> member name on the python side, e.g. <c>PERSON</c>.</summary>
    public string EntityType { get; init; } = string.Empty;

    public double Score { get; init; }

    public int StartPosition { get; init; }

    public int EndPosition { get; init; }

    public string DetectedText { get; init; } = string.Empty;

    /// <summary>1..3, after Schwartz &amp; Solove.</summary>
    public int RiskLevel { get; init; }

    /// <summary><c>PHI</c> or <c>NON_PHI</c>.</summary>
    public string HipaaCategory { get; init; } = string.Empty;
}

/// <summary>Serializer settings for the PII service's snake_case JSON.</summary>
public static class PiiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
