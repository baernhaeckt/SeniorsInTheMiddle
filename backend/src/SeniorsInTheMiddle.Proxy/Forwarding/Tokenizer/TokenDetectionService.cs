using SeniorsInTheMiddle.Proxy.Services.Pii;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// Asks the PII service what personal data a body contains, and folds its raw findings into
/// one entry per distinct value: the token, every position it occurs at, and its risk facts.
/// </summary>
public sealed class TokenDetectionService
{
    private readonly IPiiServiceClient _piiClient;

    public TokenDetectionService(IPiiServiceClient piiClient)
    {
        _piiClient = piiClient;
    }

    public async Task<TokenDetection> DetectTokensAsync(string content, CancellationToken cancellationToken)
    {
        PiiAnalyzeResult results = await _piiClient.AnalyzeAsync(content, cancellationToken);

        // Grouped by the pair, not by a concatenation of the two: "PERSON" + "Anna" and
        // "PERSONA" + "nna" must not land in one group.
        TokenDetectionResult[] tokens = results.DetectionResults
            .GroupBy(d => (d.EntityType, d.DetectedText))
            .Select(Map)
            .ToArray();

        NearMiss[] nearMisses = results.IgnoredResults
            .Select(d => new NearMiss(d.EntityType, d.DetectedText, Math.Clamp(d.Score, 0, 1)))
            .ToArray();

        return new TokenDetection(tokens, nearMisses);
    }

    private static TokenDetectionResult Map(IGrouping<(string EntityType, string DetectedText), PiiDetection> grouping)
    {
        PiiDetection entity = grouping.First();
        Token token = new(entity.DetectedText, entity.EntityType);
        TokenOccurrence[] occurrences = grouping.Select(d => new TokenOccurrence(d.StartPosition, d.Score)).ToArray();
        TokenFacts facts = new(entity.InformationType, entity.RiskLevel, entity.HipaaCategory);

        return new TokenDetectionResult(token, occurrences, facts);
    }
}
