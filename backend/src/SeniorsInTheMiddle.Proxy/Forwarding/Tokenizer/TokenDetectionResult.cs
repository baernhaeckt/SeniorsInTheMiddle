namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>One value the analyzer found, at every offset it reported it.</summary>
public record TokenDetectionResult(Token Token, IReadOnlyList<TokenOccurrence> Occurrences);

/// <summary>Where one finding sits, as the analyzer counts (code points), and how sure it was.</summary>
public record TokenOccurrence(int Position, double Score);
