using System.Text;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

public class ReplacerService : IBodyMutationFactory, IExchangeBodyMutation
{
    private readonly TokenDetectionService _tokenDetectionService;

    private readonly TokenAnonymizerService _tokenAnonymizerService;

    private readonly ITelemetrySink _telemetrySink;

    public ReplacerService(
        TokenDetectionService tokenDetectionService, 
        TokenAnonymizerService tokenAnonymizerService,
        ITelemetrySink telemetrySink)
    {
        _tokenDetectionService = tokenDetectionService;
        _tokenAnonymizerService = tokenAnonymizerService;
        _telemetrySink = telemetrySink;
    }

    /// <summary>
    /// Rewrites every detected token in <paramref name="content"/> with its anonymized stand-in.
    ///
    /// The findings arrive as spans over the text, and nothing about them guarantees the tidy
    /// left-to-right sequence that copying the gaps between them assumes. Presidio reports
    /// overlapping entities as a matter of course -- an address that contains a city, a URL that
    /// contains an e-mail, a name inside both -- and the same text classified twice lands two
    /// findings on one position. So the spans are ordered, and any that starts inside the one
    /// already written is dropped: the enclosing span was replaced whole, so the nested finding
    /// has nothing left to hide.
    /// </summary>
    public async Task<MemoryStream> AnonymizeAsync(string content, CancellationToken cancellationToken)
    {
        MemoryStream resultStream = new();

        if (content.Length == 0)
            return resultStream;

        List<(TokenDetectionResult Token, string AnonymizedValue)> foundTokens = await GetAnonymizedTokensAsync(content, cancellationToken).ToListAsync();

        int lastIndex = 0;
        foreach (TokenReplacement tokenReplacements in GetTokenReplacements(content, foundTokens)
                     .OrderBy(tr => tr.Position)
                     .ThenByDescending(tr => tr.Length))
        {
            // Nested in, or identical to, a span already replaced. Ordering by descending length
            // at equal position is what makes this keep the outermost of the two.
            if (tokenReplacements.Position < lastIndex)
                continue;

            resultStream.Write(Encoding.UTF8.GetBytes(content[lastIndex..(tokenReplacements.Position)]));
            resultStream.Write(Encoding.UTF8.GetBytes(tokenReplacements.AnonymizedValue));
            lastIndex = tokenReplacements.Position + tokenReplacements.Length;
        }

        resultStream.Write(Encoding.UTF8.GetBytes(content[lastIndex..]));
        resultStream.Position = 0;
        return resultStream;
    }

    public Task<string> DeanonymizeAsync(string content, CancellationToken cancellationToken)
    {
        return _tokenAnonymizerService.DeanonymizeTokenAsync(content, cancellationToken);
    }

    IExchangeBodyMutation IBodyMutationFactory.CreateForExchange(Uri destination)
        => this;

    private async IAsyncEnumerable<(TokenDetectionResult Token, string AnonymizedValue)> GetAnonymizedTokensAsync(string content, CancellationToken cancellationToken)
    {
        foreach (TokenDetectionResult token in await _tokenDetectionService.DetectTokensAsync(content, cancellationToken))
        {
            string anonymizedValue = await _tokenAnonymizerService.AnonymizeTokenAsync(token.Token, cancellationToken);
            yield return (token, anonymizedValue);
        }
    }

    /// <summary>
    /// The findings as spans that are known to sit on the text they describe. One whose span
    /// cannot be placed is dropped rather than applied at the reported offset: replacing the
    /// wrong run of characters corrupts the body without hiding anything.
    /// </summary>
    private IEnumerable<TokenReplacement> GetTokenReplacements(
        string content,
        IEnumerable<(TokenDetectionResult Token, string AnonymizedValue)> anonymizedTokens)
    {
        CodePointOffsetMap offsets = CodePointOffsetMap.For(content);

        foreach (var (tokenDetectionResult, anonymizedValue) in anonymizedTokens)
        {
            string detectedText = tokenDetectionResult.Token.Value;

            // Nothing to cover, and a zero-length span would still take part in the ordering and
            // the overlap decisions around it.
            if (detectedText.Length == 0)
                continue;

            foreach (int reportedPosition in tokenDetectionResult.Positions)
            {
                int position = PositionOf(content, detectedText, offsets.ToStringIndex(reportedPosition));

                if (position < 0)
                {
                    _telemetrySink.Warn(
                        $"A {tokenDetectionResult.Token.Classification} finding was dropped: its text is not at the reported position {reportedPosition}, nor anywhere else in the body.");

                    continue;
                }

                yield return new TokenReplacement(anonymizedValue, position, detectedText.Length);
            }
        }
    }

    /// <summary>
    /// Where <paramref name="detectedText"/> actually sits, given the offset the service
    /// reported for it, or -1 when it sits nowhere.
    ///
    /// The reported offset is checked rather than trusted. It is computed on the other side of a
    /// process boundary, over a copy of the text, and one that does not land on the text it
    /// claims to describe is a fact about this body -- not something to discover after the wrong
    /// characters have already been replaced.
    /// </summary>
    private static int PositionOf(string content, string detectedText, int reportedPosition)
    {
        if (reportedPosition >= 0
            && reportedPosition + detectedText.Length <= content.Length
            && content.AsSpan(reportedPosition, detectedText.Length).SequenceEqual(detectedText))
        {
            return reportedPosition;
        }

        // Off, so the next occurrence at or after it is the best remaining guess -- and then the
        // first one anywhere, for an offset that pointed past the last of them.
        int searchFrom = Math.Clamp(reportedPosition, 0, content.Length);
        int found = content.IndexOf(detectedText, searchFrom, StringComparison.Ordinal);

        return found >= 0 ? found : content.IndexOf(detectedText, StringComparison.Ordinal);
    }

    async ValueTask<byte[]?> IExchangeBodyMutation.MutateRequestAsync(
        ReadOnlyMemory<byte> body, 
        BodyDescriptor descriptor, 
        CancellationToken cancellationToken)
    {
        if (descriptor.ContentType != null && (descriptor.ContentType.Contains("json") || descriptor.ContentType.Contains("text")))
        {
            string content = Encoding.UTF8.GetString(body.Span);

            // The detection service rejects empty text, and a body with nothing in it has
            // nothing to hide either way.
            if (content.Length == 0)
                return body.ToArray();

            MemoryStream anonymizedContent = await AnonymizeAsync(content, cancellationToken);

            return anonymizedContent.ToArray();
        }

        return body.ToArray();
    }

    async ValueTask<byte[]?> IExchangeBodyMutation.MutateResponseAsync(
        ReadOnlyMemory<byte> body, 
        BodyDescriptor descriptor, 
        CancellationToken cancellationToken)
    {
        if (descriptor.ContentType != null && (descriptor.ContentType.Contains("json") || descriptor.ContentType.Contains("text")))
        {
            string content = Encoding.UTF8.GetString(body.Span);

            if (content.Length == 0)
                return body.ToArray();

            string responseContent = await DeanonymizeAsync(content, cancellationToken);

            return Encoding.UTF8.GetBytes(responseContent);
        }

        return body.ToArray();
    }
}
