using System.Collections.Concurrent;
using SeniorsInTheMiddle.Proxy.Services.Pii;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
///     Provides an anonymized value for a given token.
/// </summary>
public class TokenAnonymizerService
{
    private readonly ConcurrentDictionary<Token, Token> _anonimizationLookup = new();

    private readonly ConcurrentDictionary<Token, Token> _deanonimizationLookup = new();

    private readonly IPiiServiceClient _client;

    public TokenAnonymizerService(IPiiServiceClient client)
    {
        _client = client;
    }

    public async Task<string> AnonymizeTokenAsync(Token token, CancellationToken cancellationToken)
    {
        if (_anonimizationLookup.TryGetValue(token, out var anonymizedToken))
        {
            return anonymizedToken.Value;
        }

        Token newToken = new Token(await CreateNewAnonimizationTokenAsync(token.Value, token.Classification, cancellationToken), token.Classification); 

        // Store the mapping for future reference
        Token finalToken = _anonimizationLookup.GetOrAdd(token, newToken);

        if (finalToken != newToken)
        {
            return finalToken.Value;
        }
      
        _deanonimizationLookup.TryAdd(newToken, token);
        return newToken.Value;
    }

    private Task<string> CreateNewAnonimizationTokenAsync(string value, string classification, CancellationToken cancellationToken)
        => _client.ReplacementTextAsync(classification, cancellationToken);

    public async Task<string> DeanonymizeTokenAsync(string content, CancellationToken cancellationToken)
    {
        foreach (var deanonimizationEntry in _deanonimizationLookup)
        {
            content = content.Replace(deanonimizationEntry.Key.Value, deanonimizationEntry.Value.Value);
        }

        return content;
    }
}
