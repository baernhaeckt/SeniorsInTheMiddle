using Microsoft.Extensions.Configuration;

using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;
using SeniorsInTheMiddle.Proxy.Services.Pii;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace Backend.Tests.Unit;

/// <summary>
/// Who gets to share a stand-in map, and for how long.
///
/// A map scoped to one HTTP exchange is not enough: a chat client draws the message on screen
/// from a request that is not the one that sent it, so the map that could undo the stand-in
/// would already be collected and the person would see the fake name they never typed. The map
/// is therefore scoped to the client and host, and everything below is a limit on how far.
/// </summary>
[TestClass]
public class AnonymizerVaultTests
{
    private static readonly Uri Chat = new("https://chatgpt.com/backend-api/conversation");

    private static readonly Uri News = new("https://news.example.ch/");

    private static readonly ClientIdentity Tablet = new("Tablet|10.0.0.7");

    private static readonly ClientIdentity Laptop = new("Laptop|10.0.0.8");

    /// <summary>The bug, at the level the vault decides it: two requests from one device to one
    /// host share the map, so what the first hid the second can put back.</summary>
    [TestMethod]
    public void One_Client_And_Host_Keep_The_Same_Map_Across_Requests()
    {
        AnonymizerVault vault = Vault(out _);

        Assert.AreSame(Anonymizer(vault, Tablet, Chat), Anonymizer(vault, Tablet, Chat));
        Assert.AreEqual(1, vault.Count);
    }

    /// <summary>
    /// A path on the same host is the same session. It is the authority that is keyed on, not
    /// the URL, or posting a message and fetching the conversation back would be two sessions.
    /// </summary>
    [TestMethod]
    public void A_Different_Path_On_The_Same_Host_Is_The_Same_Map()
    {
        AnonymizerVault vault = Vault(out _);

        Assert.AreSame(
            Anonymizer(vault, Tablet, new Uri("https://chatgpt.com/backend-api/conversation")),
            Anonymizer(vault, Tablet, new Uri("https://chatgpt.com/backend-api/conversation/6f2a")));
    }

    /// <summary>
    /// Two devices never share one. The map pairs a fake value with a real one, and a shared map
    /// is one device's response being rewritten with another person's name.
    /// </summary>
    [TestMethod]
    public void Two_Clients_Never_Share_A_Map()
    {
        AnonymizerVault vault = Vault(out _);

        Assert.AreNotSame(Anonymizer(vault, Tablet, Chat), Anonymizer(vault, Laptop, Chat));
        Assert.AreEqual(2, vault.Count);
    }

    /// <summary>
    /// Nor do two hosts. This is what stops an unrelated site's "René Bauer" being rewritten
    /// into the real name that one was standing in for on the chat site.
    /// </summary>
    [TestMethod]
    public void Two_Hosts_Never_Share_A_Map()
    {
        AnonymizerVault vault = Vault(out _);

        Assert.AreNotSame(Anonymizer(vault, Tablet, Chat), Anonymizer(vault, Tablet, News));
    }

    /// <summary>Past the configured lifetime the map is gone, and what it hid is no longer
    /// restorable by anything.</summary>
    [TestMethod]
    public void A_Map_Is_Dropped_Once_Its_Lifetime_Has_Passed()
    {
        TestClock clock = new();
        AnonymizerVault vault = Vault(out _, TimeSpan.FromHours(48), clock: clock);

        TokenAnonymizerService first = Anonymizer(vault, Tablet, Chat);

        clock.Advance(TimeSpan.FromHours(47));
        Assert.AreSame(first, Anonymizer(vault, Tablet, Chat), "The map expired inside its lifetime.");

        // Counted from the last use, not from creation: the 47 hours above reset it. And the
        // deadline itself is still inside the lifetime -- 48 hours means 48, not 47:59:59.
        clock.Advance(TimeSpan.FromHours(48));
        Assert.AreSame(first, Anonymizer(vault, Tablet, Chat), "The map expired on the deadline rather than past it.");

        clock.Advance(TimeSpan.FromHours(48) + TimeSpan.FromSeconds(1));
        Assert.AreNotSame(first, Anonymizer(vault, Tablet, Chat));
    }

    /// <summary>
    /// A lifetime of zero is the old behaviour, spelled as a setting rather than as a structure
    /// nobody can change: every request starts again, and nothing a site echoes back later is
    /// ever put right.
    /// </summary>
    [TestMethod]
    public void A_Lifetime_Of_Zero_Keeps_Nothing()
    {
        TestClock clock = new();
        AnonymizerVault vault = Vault(out _, TimeSpan.Zero, clock: clock);

        TokenAnonymizerService first = Anonymizer(vault, Tablet, Chat);

        // Within the one request that created it, it is still the map that request is using.
        Assert.AreSame(first, Anonymizer(vault, Tablet, Chat));

        clock.Advance(TimeSpan.FromMilliseconds(1));

        Assert.AreNotSame(first, Anonymizer(vault, Tablet, Chat));
    }

    /// <summary>
    /// The process holds a bounded number of these, however many devices turn up. Which one goes
    /// is the one used longest ago, and it is said out loud, because a dropped map is a restore
    /// that will silently not happen.
    /// </summary>
    [TestMethod]
    public void Past_The_Client_Cap_The_Least_Recently_Used_Map_Is_Evicted()
    {
        TestClock clock = new();
        AnonymizerVault vault = Vault(out List<TelemetryEvent> events, maxClients: 2, clock: clock);

        ClientIdentity first = new("Device|10.0.0.1");
        ClientIdentity third = new("Device|10.0.0.3");

        TokenAnonymizerService oldest = Anonymizer(vault, first, Chat);
        clock.Advance(TimeSpan.FromMinutes(1));
        Anonymizer(vault, new ClientIdentity("Device|10.0.0.2"), Chat);
        clock.Advance(TimeSpan.FromMinutes(1));
        TokenAnonymizerService newest = Anonymizer(vault, third, Chat);

        // Three maps for a cap of two, and nothing has gone yet: eviction happens on a sweep,
        // and a sweep does not run on every request.
        Assert.AreEqual(3, vault.Count);

        clock.Advance(TimeSpan.FromMinutes(10));
        Anonymizer(vault, new ClientIdentity("Device|10.0.0.4"), Chat);

        Assert.AreSame(newest, Anonymizer(vault, third, Chat), "A recently used map was evicted.");
        Assert.AreNotSame(oldest, Anonymizer(vault, first, Chat), "The least recently used map survived the cap.");
        Assert.AreEqual(
            1,
            events.OfType<ProxyLog>().Count(log => log.Level == TelemetryLogLevel.Warn && log.Message.Contains("dropped before expiring")));
    }

    /// <summary>What a "forget me" control would do, and the only way a map goes early.</summary>
    [TestMethod]
    public void A_Map_Can_Be_Dropped_On_Request()
    {
        AnonymizerVault vault = Vault(out _);
        TokenAnonymizerService first = Anonymizer(vault, Tablet, Chat);

        Assert.IsTrue(vault.Forget(Tablet, Chat));
        Assert.IsFalse(vault.Forget(Tablet, Chat), "Forgetting twice reported a map that was already gone.");
        Assert.AreNotSame(first, Anonymizer(vault, Tablet, Chat));
    }

    /// <summary>The settings are read once and refused rather than clamped: a lifetime nobody
    /// meant to configure is a disclosure window nobody meant to open.</summary>
    [TestMethod]
    public void An_Unreasonable_Lifetime_Is_Refused_At_Startup()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Lifetime("-1"));
        Assert.ThrowsExactly<InvalidOperationException>(() => Lifetime((VaultLifetime.MaxTtlHours + 1).ToString()));
        Assert.AreEqual(TimeSpan.FromHours(VaultLifetime.DefaultTtlHours), Lifetime(null).Ttl);
        Assert.AreEqual(TimeSpan.FromHours(12), Lifetime("12").Ttl);
    }

    private static VaultLifetime Lifetime(string? hours)
        => VaultLifetime.From(new ConfigurationBuilder()
            .AddInMemoryCollection(hours is null ? [] : new Dictionary<string, string?> { ["Proxy:AnonymizerTtlHours"] = hours })
            .Build());

    private static AnonymizerVault Vault(
        out List<TelemetryEvent> events,
        TimeSpan? ttl = null,
        int maxClients = VaultLifetime.DefaultMaxClients,
        TimeProvider? clock = null)
    {
        events = [];

        return new AnonymizerVault(
            new VaultLifetime(ttl ?? TimeSpan.FromHours(VaultLifetime.DefaultTtlHours), maxClients),
            new CollectingSink(events),
            clock);
    }

    private static TokenAnonymizerService Anonymizer(AnonymizerVault vault, ClientIdentity client, Uri destination)
        => vault.For(client, destination, () => new TokenAnonymizerService(new UnusedPiiService(), NullSink.Instance));

    /// <summary>A clock a test moves by hand, so a two-day lifetime is tested in microseconds.</summary>
    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _timestamp, by.Ticks);
    }

    /// <summary>The vault never calls the analyzer; it only decides which map is handed out.</summary>
    private sealed class UnusedPiiService : IPiiServiceClient
    {
        public bool IsEnabled => true;

        public Task<PiiAnalyzeResult> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The vault analysed something.");

        public Task<string> ReplacementTextAsync(string piiType, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The vault asked for a stand-in.");
    }

    /// <summary>Collects published events so a test can assert on what the code emitted.</summary>
    private sealed class CollectingSink(List<TelemetryEvent> events) : ITelemetrySink
    {
        public void Publish(TelemetryEvent telemetryEvent) => events.Add(telemetryEvent);
    }

    /// <summary>Discards everything, for the tests that are not about telemetry.</summary>
    private sealed class NullSink : ITelemetrySink
    {
        public static readonly NullSink Instance = new();

        public void Publish(TelemetryEvent telemetryEvent) { }
    }
}
