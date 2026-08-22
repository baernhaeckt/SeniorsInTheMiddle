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

    public async Task<MemoryStream> AnonymizeAsync(string content, CancellationToken cancellationToken)
    {
        List<(TokenDetectionResult Token, string AnonymizedValue)> foundTokens = await GetAnonymizedTokensAsync(content, cancellationToken).ToListAsync();

        MemoryStream resultStream = new();

        int lastIndex = 0;
        foreach (TokenReplacement tokenReplacements in GetTokenReplacements(foundTokens).OrderBy(tr => tr.Position))
        {
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

    private IEnumerable<TokenReplacement> GetTokenReplacements(IEnumerable<(TokenDetectionResult Token, string AnonymizedValue)> anonymizedTokens)
    {
        foreach (var (tokenDetectionResult, anonymizedValue) in anonymizedTokens)
        {
            int length = tokenDetectionResult.Token.Value.Length;

            foreach (int position in tokenDetectionResult.Positions)
            {
                yield return new TokenReplacement(anonymizedValue, position, length);
            }
        }
    }

    async ValueTask<byte[]?> IExchangeBodyMutation.MutateRequestAsync(
        ReadOnlyMemory<byte> body, 
        BodyDescriptor descriptor, 
        CancellationToken cancellationToken)
    {
        if (descriptor.ContentType != null && (descriptor.ContentType.Contains("json") || descriptor.ContentType.Contains("text")))
        {
            string content = Encoding.UTF8.GetString(body.Span);
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
            string responseContent = await DeanonymizeAsync(content, cancellationToken);

            return Encoding.UTF8.GetBytes(responseContent);
        }

        return body.ToArray();
    }
}
