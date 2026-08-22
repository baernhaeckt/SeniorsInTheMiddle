using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>Everything the detector said about one text.</summary>
/// <param name="Tokens">Values above the confidence threshold, each at every offset reported.</param>
/// <param name="NearMisses">Values below it. Reported onwards, never replaced.</param>
public record TokenDetection(IReadOnlyList<TokenDetectionResult> Tokens, IReadOnlyList<NearMiss> NearMisses)
{
    public static readonly TokenDetection Empty = new([], []);
}

/// <summary>One value the analyzer found, at every offset it reported it.</summary>
public record TokenDetectionResult(Token Token, IReadOnlyList<TokenOccurrence> Occurrences, TokenFacts Facts);

/// <summary>Where one finding sits, as the analyzer counts (code points), and how sure it was.</summary>
public record TokenOccurrence(int Position, double Score);

/// <summary>
/// What the detector knows about a kind of value beyond its name. Kept off <see cref="Token"/>
/// on purpose: that record is the anonymizer's cache key, and these facts do not make two
/// tokens different.
/// </summary>
public record TokenFacts(string InformationType, int RiskLevel, string HipaaCategory)
{
    public static readonly TokenFacts Unknown = new(string.Empty, 0, string.Empty);
}
