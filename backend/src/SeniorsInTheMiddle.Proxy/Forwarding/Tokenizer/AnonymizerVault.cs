using System.Collections.Concurrent;

using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

/// <summary>
/// Keeps one stand-in map per client, for as long as <see cref="VaultLifetime"/> says.
///
/// It exists because the two halves of a rewrite are not always the two halves of one HTTP
/// exchange. A chat client posts a message and is answered by an event stream; the message it
/// then draws on the screen comes back in a *different* request -- the conversation it re-fetches,
/// the history it syncs, the page it reloads. A map scoped to the exchange that created it is
/// already gone by then, so the stand-in reaches the screen and the person sees a name that is
/// not theirs. That was the bug this replaces.
///
/// The key is the client and the host together, and the host is not decoration. Widening the map
/// from one exchange to one session is already a real widening: a stand-in registered here will
/// now be put back in a body that was not the one it was made for. Keeping the host in the key is
/// what stops the worst version of that -- someone else's "René Bauer" on an unrelated site being
/// rewritten into this user's real name -- while costing the chat case nothing, because a response
/// comes back from the host its request went to.
///
/// Everything else about the map's own safety is <see cref="TokenAnonymizerService"/>'s.
/// </summary>
public sealed class AnonymizerVault
{
    /// <summary>
    /// How often expiry is looked for. Sweeping on every request would walk the whole dictionary
    /// under a lock the request does not need; sweeping never would keep a map alive by the
    /// absence of traffic, which is exactly when it should be going away.
    /// </summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private readonly VaultLifetime _lifetime;

    private readonly ITelemetrySink _telemetrySink;

    private readonly TimeProvider _clock;

    private long _sweptAt;

    public AnonymizerVault(VaultLifetime lifetime, ITelemetrySink telemetrySink, TimeProvider? clock = null)
    {
        _lifetime = lifetime;
        _telemetrySink = telemetrySink;
        _clock = clock ?? TimeProvider.System;
        _sweptAt = _clock.GetTimestamp();
    }

    /// <summary>How many client maps are being held. For the tests and for a health view.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// The map for <paramref name="client"/> talking to <paramref name="destination"/>, created
    /// by <paramref name="create"/> if this is the first anyone has heard of that pair.
    ///
    /// A lifetime of zero is not a special case with a shortcut: it takes the ordinary path and
    /// the entry is expired by the time the next request looks for it, so the two configurations
    /// differ in one number and nowhere else. What makes that work rather than spin is that
    /// expiry is strictly past the deadline -- a map created for this request has aged nothing
    /// yet, so it is this request's however short the lifetime.
    /// </summary>
    public TokenAnonymizerService For(ClientIdentity client, Uri destination, Func<TokenAnonymizerService> create)
    {
        Sweep();

        string key = KeyOf(client, destination);
        long now = _clock.GetTimestamp();

        // Two passes are all this needs: the first finds an expired map and drops it, the second
        // creates one, which cannot be expired. The bound is for the case that does not fit that
        // -- another request replacing the entry between the two -- where giving up and handing
        // out an unshared map is better than looping.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Entry entry = _entries.GetOrAdd(key, _ => new Entry(create(), now));

            // Expired between the sweep above and this read, which two requests arriving either
            // side of the deadline will do. Replacing it rather than reusing it is the whole
            // point of the deadline.
            if (!IsExpired(entry, now))
            {
                entry.LastUsed = now;

                return entry.Anonymizer;
            }

            _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }

        return create();
    }

    /// <summary>
    /// Drops the map for one client and host, so a value hidden before is no longer restorable.
    /// Nothing in the proxy calls this yet; it is what a "forget me" control would.
    /// </summary>
    public bool Forget(ClientIdentity client, Uri destination)
        => _entries.TryRemove(KeyOf(client, destination), out _);

    private static string KeyOf(ClientIdentity client, Uri destination)
        // A null separator, because neither half can contain one and a hostname can contain
        // anything else a separator might be spelled with.
        => $"{client.Value}\0{destination.Host}";

    /// <summary>Strictly past the deadline, so a map that has aged nothing is never expired --
    /// which is what keeps a lifetime of zero meaning "this exchange only" rather than "no map
    /// can ever be handed out".</summary>
    private bool IsExpired(Entry entry, long now)
        => _clock.GetElapsedTime(entry.LastUsed, now) > _lifetime.Ttl;

    /// <summary>
    /// Drops what has expired, and then, if the process is holding more clients than it is
    /// allowed to, the ones used longest ago until it is not.
    ///
    /// The cap is enforced after expiry rather than instead of it so that the common case -- a
    /// handful of devices, none of them near the cap -- never sorts anything.
    /// </summary>
    private void Sweep()
    {
        long now = _clock.GetTimestamp();
        long sweptAt = Volatile.Read(ref _sweptAt);

        if (_clock.GetElapsedTime(sweptAt, now) < SweepInterval)
            return;

        // One sweeper at a time. A racing caller sees the updated stamp and goes straight on to
        // its own request rather than walking the same dictionary again.
        if (Interlocked.CompareExchange(ref _sweptAt, now, sweptAt) != sweptAt)
            return;

        foreach ((string key, Entry entry) in _entries)
        {
            // Expiry is the map doing what it was configured to do, so it is not announced.
            // Eviction below is not, and is.
            if (IsExpired(entry, now))
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
        }

        int evicted = 0;

        while (_entries.Count > _lifetime.MaxClients)
        {
            KeyValuePair<string, Entry> oldest = _entries.MinBy(pair => pair.Value.LastUsed);

            // Emptied underneath us; there is nothing left to evict.
            if (oldest.Value is null)
                break;

            if (_entries.TryRemove(oldest))
                evicted++;
        }

        if (evicted > 0)
        {
            _telemetrySink.Warn(
                $"{evicted} client stand-in map(s) were dropped before expiring: more than {_lifetime.MaxClients} clients are talking through the proxy, "
                + "so values hidden for the least recently seen of them can no longer be put back.");
        }
    }

    /// <summary>One client's map and when it was last handed out. Mutable on purpose: the
    /// timestamp is written on every request and the dictionary entry is not replaced for it.</summary>
    private sealed class Entry(TokenAnonymizerService anonymizer, long lastUsed)
    {
        public TokenAnonymizerService Anonymizer { get; } = anonymizer;

        public long LastUsed { get; set; } = lastUsed;
    }
}
