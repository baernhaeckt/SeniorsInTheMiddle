using System.Collections.Concurrent;
using System.Text;

using SeniorsInTheMiddle.Proxy.Services.Pii;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// Provides an anonymized stand-in for a token and puts it back afterwards.
///
/// One instance per client and destination host, kept by <see cref="AnonymizerVault"/> for as
/// long as <see cref="VaultLifetime"/> says. The map from stand-in to real value is the most
/// sensitive thing in the process: applied to a body it was not built for, it writes someone's
/// real name into an unrelated response wherever the fake one happens to occur. What keeps that
/// in hand is the key rather than the lifetime -- a response can only be rewritten with values
/// this client hid on this host -- because the lifetime cannot be short enough to be safe and
/// still be useful. A chat client draws the message it sent from a request that is not the one
/// that sent it, so a map that dies with its exchange restores nothing at all.
/// </summary>
public sealed class TokenAnonymizerService
{
    /// <summary>
    /// How often a fresh stand-in is requested when the one returned already stands for a
    /// different value. The faker draws at random from a small pool, so a collision is rare
    /// but not impossible, and a second draw almost always resolves it.
    /// </summary>
    private const int CollisionRetries = 3;

    /// <summary>
    /// How many pairs one client's map holds before it stops taking new ones.
    ///
    /// A map that lives for days accumulates every distinct value that client ever sent to that
    /// host, and there is no natural end to that. Refusing new pairs rather than evicting old
    /// ones is the direction that cannot break a restore that already works: what was hidden
    /// yesterday still comes back today, and what stops is only the ability to restore something
    /// hidden past the cap -- which is said out loud when it happens. Hiding is never refused.
    /// </summary>
    private const int MaxPairs = 4096;

    private readonly ConcurrentDictionary<Token, Token> _anonymizationLookup = new();

    private readonly ConcurrentDictionary<Token, Token> _deanonymizationLookup = new();

    private readonly IPiiServiceClient _client;

    private readonly ITelemetrySink _telemetrySink;

    /// <summary>
    /// The search forms of every stand-in, rebuilt when the map changes rather than on every
    /// chunk of a stream. Volatile because a streaming response reads it while the request half
    /// of another exchange on the same client is still adding to the map.
    /// </summary>
    private volatile Restorations? _restorations;

    private int _capReported;

    public TokenAnonymizerService(IPiiServiceClient client, ITelemetrySink telemetrySink)
    {
        _client = client;
        _telemetrySink = telemetrySink;
    }

    /// <summary>Whether anything has been hidden that could be put back.</summary>
    public bool HasStandIns => !_deanonymizationLookup.IsEmpty;

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

            if (Register(candidate, token))
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
            if (_deanonymizationLookup.TryRemove(newToken, out _))
                _restorations = null;
        }

        return finalToken.Value;
    }

    /// <summary>
    /// The content with every known stand-in put back, and how many were.
    ///
    /// One pass over the text. Longer stand-ins win where two overlap, so one that contains
    /// another is restored whole rather than having its inside replaced first.
    /// </summary>
    public Task<(string Content, int Restored)> DeanonymizeTokenAsync(
        string content,
        CancellationToken cancellationToken,
        BodySyntax syntax = BodySyntax.Text)
        => Task.FromResult(Deanonymize(content, syntax));

    /// <summary>
    /// The same, without the task. A restore is a pass over a string in memory; the async
    /// signature above is what the mutation interface asks for, not something that awaits.
    /// </summary>
    public (string Content, int Restored) Deanonymize(string content, BodySyntax syntax)
    {
        if (content.Length == 0)
            return (content, 0);

        IReadOnlyList<Restoration> restorations = Search(syntax);

        if (restorations.Count == 0)
            return (content, 0);

        List<(int Start, int Length, string Real)> matches = [];

        foreach (Restoration restoration in restorations)
        {
            string value = restoration.StandIn;

            for (int index = content.IndexOf(value, StringComparison.Ordinal);
                 index >= 0;
                 index = content.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            {
                matches.Add((index, value.Length, restoration.Real));
            }
        }

        if (matches.Count == 0)
            return (content, 0);

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

        return (restored.ToString(), count);
    }

    /// <summary>
    /// How many characters at the end of <paramref name="text"/> a streaming restore must hold
    /// back, because they could still turn out to be the start of a stand-in once the next chunk
    /// arrives.
    ///
    /// This is what makes a restore over a stream give the same answer as one over the whole
    /// body, for any stand-in that arrives in one piece. Holding back a fixed window instead
    /// would be simpler and wrong in both directions: too small and a name split across a packet
    /// boundary escapes, too large and the last characters of an idle event stream sit in a
    /// buffer waiting for traffic that is not coming. The answer here is almost always zero.
    ///
    /// Input:  "...am 17. Dez", stand-in "René Bauer"  -&gt; 0
    /// Input:  "...heisst Ren", stand-in "René Bauer"  -&gt; 3
    /// </summary>
    public int HoldBack(string text, BodySyntax syntax)
    {
        int longest = 0;

        foreach (Restoration restoration in Search(syntax))
        {
            string standIn = restoration.StandIn;

            // A full match is not held back -- it is a match, and this pass would only delay it.
            for (int length = Math.Min(standIn.Length - 1, text.Length); length > longest; length--)
            {
                if (string.CompareOrdinal(text, text.Length - length, standIn, 0, length) == 0)
                {
                    longest = length;

                    break;
                }
            }
        }

        return longest;
    }

    /// <summary>
    /// Puts <paramref name="candidate"/> in the restore map for <paramref name="token"/>, unless
    /// it already stands for something else or the map is full. True means the stand-in is this
    /// token's and a restore will find it.
    /// </summary>
    private bool Register(Token candidate, Token token)
    {
        if (_deanonymizationLookup.TryGetValue(candidate, out Token? existing))
            return existing == token;

        if (_deanonymizationLookup.Count >= MaxPairs)
        {
            // Once, per map. The condition holds for every value from here on, and a warning per
            // detected entity would bury the one that matters.
            if (Interlocked.Exchange(ref _capReported, 1) == 0)
            {
                _telemetrySink.Warn(
                    $"This client's stand-in map has reached {MaxPairs} values for this host. Everything found is still hidden, "
                    + "but nothing hidden from here on can be put back until the map expires.");
            }

            return false;
        }

        if (!_deanonymizationLookup.TryAdd(candidate, token))
            return _deanonymizationLookup.TryGetValue(candidate, out Token? owner) && owner == token;

        _restorations = null;

        return true;
    }

    /// <summary>
    /// What to look for and what to write in its place, for a body of this syntax.
    ///
    /// Cached against the map it was built from, because a streaming response asks for this once
    /// per chunk and the map changes a few times per request at most.
    /// </summary>
    private IReadOnlyList<Restoration> Search(BodySyntax syntax)
    {
        Restorations? cached = _restorations;

        if (cached is null)
        {
            cached = Build();
            _restorations = cached;
        }

        return syntax == BodySyntax.Json ? cached.Json : cached.Text;
    }

    /// <summary>
    /// The map as search forms.
    ///
    /// A stand-in was written into the request JSON-escaped, and a restore that looks for the
    /// unescaped form finds it anyway for an ordinary name, because escaping leaves an ordinary
    /// name alone. What it does not find is the same name written by a different JSON writer:
    /// Python's json.dumps escapes every non-ASCII character by default, so the "René Bauer"
    /// this proxy sent comes back as "René Bauer" and a literal search misses it entirely.
    /// That is not a corner case; it is what a Python-backed chat API answers with.
    ///
    /// So a JSON body is searched for both spellings, and -- this is the half that matters as
    /// much -- the real value is written back in the spelling that was matched. Splicing a raw
    /// value into a JSON string is how a name with a quote in it turns a response into something
    /// the client cannot parse.
    /// </summary>
    private Restorations Build()
    {
        List<Restoration> text = [];
        List<Restoration> json = [];

        foreach ((Token standIn, Token real) in _deanonymizationLookup)
        {
            if (standIn.Value.Length == 0)
                continue;

            text.Add(new Restoration(standIn.Value, real.Value));

            string relaxed = JsonText.Escape(standIn.Value, asciiOnly: false);
            json.Add(new Restoration(relaxed, JsonText.Escape(real.Value, asciiOnly: false)));

            string ascii = JsonText.Escape(standIn.Value, asciiOnly: true);

            // Identical for a stand-in that is already ASCII, which most are.
            if (!string.Equals(ascii, relaxed, StringComparison.Ordinal))
                json.Add(new Restoration(ascii, JsonText.Escape(real.Value, asciiOnly: true)));
        }

        return new Restorations(text, json);
    }

    /// <summary>One thing to look for in a response, and what to write where it is found.</summary>
    private sealed record Restoration(string StandIn, string Real);

    /// <summary>
    /// The same restorations spelled two ways, because a response may carry either: raw text,
    /// and the JSON-escaped form (both relaxed and ASCII-only) for string literals.
    /// </summary>
    private sealed record Restorations(IReadOnlyList<Restoration> Text, IReadOnlyList<Restoration> Json);
}

/// <summary>
/// How a body spells the text inside it, which decides what a stand-in looks like on the wire
/// and how the real value has to be written back in its place.
/// </summary>
public enum BodySyntax
{
    /// <summary>Characters mean themselves. Plain text, HTML, anything not below.</summary>
    Text,

    /// <summary>Text lives inside JSON string literals -- a JSON body, and an event stream,
    /// whose frames carry JSON in every chat protocol this proxy has met.</summary>
    Json,
}
