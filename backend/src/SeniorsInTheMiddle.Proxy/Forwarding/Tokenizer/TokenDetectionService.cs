using SeniorsInTheMiddle.Proxy.Services.Pii;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

public class TokenDetectionService
{
    private readonly IPiiServiceClient _piiClient;

    public TokenDetectionService(IPiiServiceClient piiClient)
    {
        _piiClient = piiClient;
    }

    public async Task<IEnumerable<TokenDetectionResult>> DetectTokensAsync(string content, CancellationToken cancellationToken)
    {
        PiiAnalyzeResult results = await _piiClient.AnalyzeAsync(content, cancellationToken);
        IEnumerable<TokenDetectionResult> mappedResults = results.DetectionResults.GroupBy(d => $"{d.EntityType}{d.DetectedText}").Select(Map);

        return mappedResults;
    }

    private TokenDetectionResult Map(IGrouping<string, PiiDetection> grouping)
    {
        PiiDetection entity = grouping.First();
        Token token = new(entity.DetectedText, entity.EntityType);
        TokenOccurrence[] occurrences = grouping.Select(d => new TokenOccurrence(d.StartPosition, d.Score)).ToArray();

        return new TokenDetectionResult(token, occurrences);
    }
}
