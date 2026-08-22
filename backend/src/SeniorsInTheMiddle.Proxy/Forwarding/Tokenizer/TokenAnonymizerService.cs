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

    /// <summary>The content with every known stand-in put back, and how many were.</summary>
    public Task<(string Content, int Restored)> DeanonymizeTokenAsync(string content, CancellationToken cancellationToken)
    {
        int restored = 0;

        foreach (var deanonimizationEntry in _deanonimizationLookup)
        {
            string token = deanonimizationEntry.Key.Value;
            int occurrences = CountOccurrences(content, token);
            if (occurrences == 0)
                continue;

            restored += occurrences;
            content = content.Replace(token, deanonimizationEntry.Value.Value);
        }

        return Task.FromResult((content, restored));
    }

    private static int CountOccurrences(string content, string value)
    {
        if (value.Length == 0)
            return 0;

        int count = 0;
        for (int index = content.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = content.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
