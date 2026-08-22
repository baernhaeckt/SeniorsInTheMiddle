using System.Collections.Concurrent;
using System.Text;

using SeniorsInTheMiddle.Proxy.Services.Pii;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// Provides an anonymized stand-in for a token and puts it back afterwards.
///
/// One instance per exchange, on purpose. The map from stand-in to real value is the most
/// sensitive thing in the process: applied to a body it was not built for, it writes someone's
/// real name into an unrelated response wherever the fake one happens to occur. Scoping it to
/// the request that created it and the response that answers it is what makes that impossible,
/// and keeps the map a handful of entries rather than everything the process ever saw.
/// </summary>
public sealed class TokenAnonymizerService
{
    /// <summary>
    /// How often a fresh stand-in is requested when the one returned already stands for a
    /// different value. The faker draws at random from a small pool, so a collision is rare
    /// but not impossible, and a second draw almost always resolves it.
    /// </summary>
    private const int CollisionRetries = 3;

    private readonly ConcurrentDictionary<Token, Token> _anonymizationLookup = new();

    private readonly ConcurrentDictionary<Token, Token> _deanonymizationLookup = new();

    private readonly IPiiServiceClient _client;

    private readonly ITelemetrySink _telemetrySink;

    public TokenAnonymizerService(IPiiServiceClient client, ITelemetrySink telemetrySink)
    {
        _client = client;
        _telemetrySink = telemetrySink;
    }

    /// <summary>
    /// The stand-in for <paramref name="token"/>: the same one every time for the same value,
    /// and never one that already stands for a different value.
    /// </summary>
    public async Task<string> AnonymizeTokenAsync(Token token, CancellationToken cancellationToken)
    {
        if (_anonymizationLookup.TryGetValue(token, out Token? anonymizedToken))
            return anonymizedToken.Value;

        Token? newToken = null;
        Token? lastCandidate = null;

        // The faker is asked for a value by type only, so it cannot know which values are
        // already taken here. A stand-in that is, is asked for again; the pool is large enough
        // that a few draws settle it.
        for (int attempt = 0; attempt <= CollisionRetries; attempt++)
        {
            Token candidate = new(await _client.ReplacementTextAsync(token.Classification, cancellationToken), token.Classification);
            lastCandidate = candidate;

            if (_deanonymizationLookup.TryAdd(candidate, token)
                || _deanonymizationLookup.TryGetValue(candidate, out Token? owner) && owner == token)
            {
                // Free, or already registered for this very value by a concurrent caller.
                newToken = candidate;
                break;
            }
        }

        bool collided = newToken is null;

        if (collided)
        {
            // Every draw stood for another value already. Hiding the value still comes first,
            // so the stand-in is used anyway; what is lost is an unambiguous restore, and that
            // is said out loud rather than discovered in a response.
            newToken = lastCandidate!;

            _telemetrySink.Warn(
                $"The stand-in for a {token.Classification} value collided with another value's {CollisionRetries + 1} times; both are hidden as \"{newToken.Value}\", and a restore yields the first.");
        }

        Token finalToken = _anonymizationLookup.GetOrAdd(token, newToken);

        if (finalToken != newToken && !collided)
        {
            // Lost a race with a concurrent call for the same token: its stand-in wins, ours
            // must not linger in the restore map.
            _deanonymizationLookup.TryRemove(newToken, out _);
        }

        return finalToken.Value;
    }

    /// <summary>
    /// The content with every known stand-in put back, and how many were.
    ///
    /// One pass over the text. Longer stand-ins win where two overlap, so one that contains
    /// another is restored whole rather than having its inside replaced first.
    /// </summary>
    public Task<(string Content, int Restored)> DeanonymizeTokenAsync(string content, CancellationToken cancellationToken)
    {
        if (_deanonymizationLookup.IsEmpty || content.Length == 0)
            return Task.FromResult((content, 0));

        List<(int Start, int Length, string Real)> matches = [];

        foreach ((Token standIn, Token real) in _deanonymizationLookup)
        {
            string value = standIn.Value;
            if (value.Length == 0)
                continue;

            for (int index = content.IndexOf(value, StringComparison.Ordinal);
                 index >= 0;
                 index = content.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            {
                matches.Add((index, value.Length, real.Value));
            }
        }

        if (matches.Count == 0)
            return Task.FromResult((content, 0));

        matches.Sort((left, right) => left.Start != right.Start
            ? left.Start.CompareTo(right.Start)
            : right.Length.CompareTo(left.Length));

        StringBuilder restored = new(content.Length);
        int lastIndex = 0;
        int count = 0;

        foreach ((int start, int length, string real) in matches)
        {
            if (start < lastIndex)
                continue;

            restored.Append(content, lastIndex, start - lastIndex).Append(real);
            lastIndex = start + length;
            count++;
        }

        restored.Append(content, lastIndex, content.Length - lastIndex);

        return Task.FromResult((restored.ToString(), count));
    }
}
