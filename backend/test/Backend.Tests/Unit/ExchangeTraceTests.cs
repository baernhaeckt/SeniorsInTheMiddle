using System.Text;

using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace Backend.Tests.Unit;

/// <summary>
/// The order and the completeness of what one request publishes, which is what the dashboard
/// relies on: <c>request.observed</c> first and once, the seven exchange events in sequence
/// for a treated request and none of them otherwise, <c>request.completed</c> last.
///
/// The awkward part under test is that the treatment is decided after the body was scanned,
/// so the announcement is held back -- and must still come out, and come out first, on every
/// path including the ones that never reach a decision.
/// </summary>
[TestClass]
public class ExchangeTraceTests
{
    private static readonly RequestFacts Facts = new(
        "10.0.0.7",
        "Tablet · .7",
        "POST",
        TelemetryScheme.Https,
        "api.example.ch",
        "/claims",
        "application/json",
        42);

    private static readonly BodyDescriptor Json = new("application/json", Encoding.UTF8);

    [TestMethod]
    public void A_Request_Without_A_Body_Is_Passthrough_And_Nothing_Else()
    {
        (ExchangeTrace trace, List<TelemetryEvent> events) = Trace();

        trace.Passthrough("no body");
        trace.Dispatched("https://api.example.ch", 0);
        trace.Responded(200, string.Empty);
        trace.Completed(200, 17, 3.5);

        CollectionAssert.AreEqual(new[] { "request.observed", "request.completed" }, Types(events));

        RequestObserved observed = events.OfType<RequestObserved>().Single();
        Assert.AreEqual(Treatment.Passthrough, observed.Treatment);
        Assert.AreEqual("no body", observed.Reason);
        Assert.IsNull(observed.ExchangeId);

        RequestCompleted completed = events.OfType<RequestCompleted>().Single();
        Assert.AreEqual(200, completed.Status);
        Assert.AreEqual(17, completed.ResponseBytes);
        Assert.AreEqual(3.5, completed.DurationMs);
    }

    [TestMethod]
    public void A_Body_Scanned_And_Found_Clean_Does_Not_Open_An_Exchange()
    {
        (ExchangeTrace trace, List<TelemetryEvent> events) = Trace();

        trace.BodyBuffered("{\"note\":\"hello\"}"u8.ToArray(), Json);
        trace.Detected([], new DetectionStats(4, 0, []));
        trace.RequestRewritten(null, Json);
        trace.Dispatched("https://api.example.ch", 16);
        trace.Responded(200, "{}");
        trace.Restored("{}", 0);
        trace.Completed(200, 2, 9);

        CollectionAssert.AreEqual(new[] { "request.observed", "request.completed" }, Types(events));

        RequestObserved observed = events.OfType<RequestObserved>().Single();
        Assert.AreEqual(Treatment.Clean, observed.Treatment);
        StringAssert.Contains(observed.Reason, "nothing found");
        StringAssert.Contains(observed.Reason, "application/json");
    }

    [TestMethod]
    public void A_Clean_Request_Names_Its_Near_Misses_In_The_Reason()
    {
        (ExchangeTrace trace, List<TelemetryEvent> events) = Trace();

        trace.BodyBuffered("{\"city\":\"Bern\"}"u8.ToArray(), Json);
        trace.Detected([], new DetectionStats(2, 0, [new NearMiss("LOCATION", "Bern", 0.4)]));
        trace.RequestRewritten(null, Json);
        trace.Completed(200, 2, 3);

        StringAssert.Contains(events.OfType<RequestObserved>().Single().Reason, "(1 near miss)");
    }

    [TestMethod]
    public void A_Treated_Request_Publishes_The_Whole_Lifecycle_In_Order()
    {
        (ExchangeTrace trace, List<TelemetryEvent> events) = Trace();
        const string body = "{\"name\":\"Hans Meier\"}";
        DetectedEntity entity = new("e1", "PERSON", "Hans Meier", "[PERSON_1]", 9, 19, 0.85);

        trace.BodyBuffered(Encoding.UTF8.GetBytes(body), Json);
        trace.Detected([entity], new DetectionStats(12.5, 1, [new NearMiss("LOCATION", "Bern", 0.4)]));
        trace.RequestRewritten("{\"name\":\"[PERSON_1]\"}"u8.ToArray(), Json);
        trace.Dispatched("https://api.example.ch", 21);
        trace.Responded(201, string.Empty);
        trace.Responded(201, "{\"greeting\":\"Hallo [PERSON_1]\"}");
        trace.Restored("{\"greeting\":\"Hallo Hans Meier\"}", 1);
        trace.Completed(201, 30, 40);

        CollectionAssert.AreEqual(
            new[]
            {
                "request.observed",
                "exchange.opened",
                "detection.completed",
                "redaction.completed",
                "upstream.dispatched",
                "upstream.responded",
                "rehydration.completed",
                "exchange.delivered",
                "request.completed",
            },
            Types(events));

        RequestObserved observed = events.OfType<RequestObserved>().Single();
        Assert.AreEqual(Treatment.Treated, observed.Treatment);
        Assert.AreEqual("1 identifier", observed.Reason);
        Assert.IsNotNull(observed.ExchangeId);

        string exchangeId = observed.ExchangeId!;
        ExchangeOpened opened = events.OfType<ExchangeOpened>().Single();
        Assert.AreEqual(exchangeId, opened.ExchangeId);
        Assert.AreEqual(observed.RequestId, opened.RequestId);
        Assert.AreEqual(body, opened.RequestBody);
        Assert.AreEqual("application/json", opened.ContentType);

        DetectionCompleted detection = events.OfType<DetectionCompleted>().Single();
        Assert.AreEqual(exchangeId, detection.ExchangeId);
        Assert.AreEqual(12.5, detection.ScannedMs);
        Assert.AreSame(entity, detection.Entities.Single());
        Assert.AreEqual(0.85, detection.RiskScoreMean);
        Assert.AreEqual(1, detection.TypeFrequencies["PERSON"]);
        Assert.AreEqual(1, detection.Suppressed);
        Assert.AreEqual("Bern", detection.NearMisses.Single().Value);

        Assert.AreEqual("{\"name\":\"[PERSON_1]\"}", events.OfType<RedactionCompleted>().Single().RedactedRequestBody);

        UpstreamDispatched dispatched = events.OfType<UpstreamDispatched>().Single();
        Assert.AreEqual("https://api.example.ch", dispatched.Target);
        Assert.AreEqual(21, dispatched.Bytes);

        UpstreamResponded responded = events.OfType<UpstreamResponded>().Single();
        Assert.AreEqual(201, responded.Status);
        Assert.AreEqual("{\"greeting\":\"Hallo [PERSON_1]\"}", responded.TokenizedResponseBody, "The later, fuller report wins.");
        Assert.IsTrue(responded.UpstreamMs >= 0);

        RehydrationCompleted rehydrated = events.OfType<RehydrationCompleted>().Single();
        Assert.AreEqual("{\"greeting\":\"Hallo Hans Meier\"}", rehydrated.ResponseBody);
        Assert.AreEqual(1, rehydrated.Restored);

        ExchangeDelivered delivered = events.OfType<ExchangeDelivered>().Single();
        Assert.IsTrue(delivered.TotalMs >= 0);
        ExchangeTiming timing = delivered.Timing;
        Assert.IsTrue(timing.BufferMs >= 0 && timing.DetectMs >= 0 && timing.UpstreamMs >= 0 && timing.RehydrateMs >= 0);
        Assert.IsTrue(timing.OverheadMs >= 0);
        Assert.IsTrue(
            timing.BufferMs + timing.DetectMs + timing.UpstreamMs + timing.RehydrateMs + timing.OverheadMs <= delivered.TotalMs + 0.001,
            "The steps never add up to more than the whole.");

        long[] times = events.Select(At).ToArray();
        CollectionAssert.AreEqual(times.OrderBy(t => t).ToArray(), times, "Timestamps never go backwards.");
        Assert.IsTrue(events.All(e => (ExchangeIdOf(e) ?? exchangeId) == exchangeId), "Every exchange event carries the one id.");
    }

    /// <summary>A request the forwarder gave up on before the body was ever looked at -- an
    /// unreachable destination, say -- is still announced, and announced before it completes.</summary>
    [TestMethod]
    public void Completion_Without_A_Decision_Still_Announces_The_Request_First()
    {
        (ExchangeTrace trace, List<TelemetryEvent> events) = Trace();

        trace.Completed(502, 0, 1);

        CollectionAssert.AreEqual(new[] { "request.observed", "request.completed" }, Types(events));
        Assert.AreEqual("not inspected", events.OfType<RequestObserved>().Single().Reason);
        Assert.AreEqual(502, events.OfType<RequestCompleted>().Single().Status);
    }

    /// <summary>The response to a treated request may be one the proxy cannot read -- an image,
    /// a stream -- and the packet on the band must still arrive, not hang at the gate.</summary>
    [TestMethod]
    public void A_Treated_Request_With_An_Unreadable_Response_Still_Reaches_Delivery()
    {
        (ExchangeTrace trace, List<TelemetryEvent> events) = Trace();

        trace.BodyBuffered("Hans"u8.ToArray(), Json);
        trace.Detected([new DetectedEntity("e1", "PERSON", "Hans", "[P]", 0, 4, 1)], new DetectionStats(1, 0, []));
        trace.RequestRewritten("[P]"u8.ToArray(), Json);
        trace.Dispatched("https://api.example.ch", 3);
        trace.Responded(200, string.Empty);
        trace.Completed(200, 5000, 80);

        CollectionAssert.AreEqual(
            new[]
            {
                "request.observed",
                "exchange.opened",
                "detection.completed",
                "redaction.completed",
                "upstream.dispatched",
                "upstream.responded",
                "rehydration.completed",
                "exchange.delivered",
                "request.completed",
            },
            Types(events));

        Assert.AreEqual(string.Empty, events.OfType<UpstreamResponded>().Single().TokenizedResponseBody);
        Assert.AreEqual(0, events.OfType<RehydrationCompleted>().Single().Restored);
    }

    /// <summary>A body the mutation could not rewrite is not forwarded, and the dashboard is
    /// told so in its own terms: a blocked line in the ticker, and a request that completes
    /// with the 502 the client got.</summary>
    [TestMethod]
    public void A_Refused_Request_Is_Announced_And_Logged_As_Blocked()
    {
        (ExchangeTrace trace, List<TelemetryEvent> events) = Trace();

        trace.BodyBuffered("{}"u8.ToArray(), Json);
        trace.RequestRefused(new InvalidOperationException("boom"));
        trace.Completed(502, 0, 2);

        CollectionAssert.AreEqual(new[] { "request.observed", "log", "request.completed" }, Types(events));

        Assert.AreEqual(Treatment.Passthrough, events.OfType<RequestObserved>().Single().Treatment);
        StringAssert.Contains(events.OfType<RequestObserved>().Single().Reason, "not forwarded");

        ProxyLog log = events.OfType<ProxyLog>().Single();
        Assert.AreEqual(TelemetryLogLevel.Block, log.Level);
        StringAssert.Contains(log.Message, "api.example.ch");
        StringAssert.Contains(log.Message, "boom");
    }

    [TestMethod]
    public void Bodies_In_Events_Are_Capped()
    {
        (ExchangeTrace trace, List<TelemetryEvent> events) = Trace();
        string body = new('x', ExchangeTrace.MaxBodyChars + 100);

        trace.BodyBuffered(Encoding.UTF8.GetBytes(body), Json);
        trace.Detected([new DetectedEntity("e1", "PERSON", "x", "[P]", 0, 1, 1)], new DetectionStats(1, 0, []));
        trace.RequestRewritten(Encoding.UTF8.GetBytes(body), Json);
        trace.Completed(200, 0, 1);

        string opened = events.OfType<ExchangeOpened>().Single().RequestBody;
        Assert.AreEqual(ExchangeTrace.MaxBodyChars + 1, opened.Length);
        Assert.IsTrue(opened.EndsWith('…'));
    }

    /// <summary>Nothing a late or repeated report can do changes what was already said.</summary>
    [TestMethod]
    public void Reports_After_Completion_Are_Ignored()
    {
        (ExchangeTrace trace, List<TelemetryEvent> events) = Trace();

        trace.Passthrough("no body");
        trace.Completed(200, 0, 1);
        trace.Passthrough("again");
        trace.Completed(500, 0, 1);
        trace.Restored("x", 1);

        Assert.AreEqual(2, events.Count);
    }

    private static (ExchangeTrace Trace, List<TelemetryEvent> Events) Trace()
    {
        List<TelemetryEvent> events = [];

        return (new ExchangeTrace(new CollectingSink(events), "r-1", Facts), events);
    }

    private static string[] Types(IEnumerable<TelemetryEvent> events)
        => events.Select(e => e switch
        {
            RequestObserved => "request.observed",
            RequestCompleted => "request.completed",
            ExchangeOpened => "exchange.opened",
            DetectionCompleted => "detection.completed",
            RedactionCompleted => "redaction.completed",
            UpstreamDispatched => "upstream.dispatched",
            UpstreamResponded => "upstream.responded",
            RehydrationCompleted => "rehydration.completed",
            ExchangeDelivered => "exchange.delivered",
            ProxyLog => "log",
            _ => e.GetType().Name,
        }).ToArray();

    private static long At(TelemetryEvent e) => e switch
    {
        RequestObserved x => x.At,
        RequestCompleted x => x.At,
        ExchangeOpened x => x.At,
        DetectionCompleted x => x.At,
        RedactionCompleted x => x.At,
        UpstreamDispatched x => x.At,
        UpstreamResponded x => x.At,
        RehydrationCompleted x => x.At,
        ExchangeDelivered x => x.At,
        ProxyLog x => x.At,
        _ => 0,
    };

    private static string? ExchangeIdOf(TelemetryEvent e) => e switch
    {
        RequestObserved x => x.ExchangeId,
        ExchangeOpened x => x.ExchangeId,
        DetectionCompleted x => x.ExchangeId,
        RedactionCompleted x => x.ExchangeId,
        UpstreamDispatched x => x.ExchangeId,
        UpstreamResponded x => x.ExchangeId,
        RehydrationCompleted x => x.ExchangeId,
        ExchangeDelivered x => x.ExchangeId,
        _ => null,
    };

    private sealed class CollectingSink(List<TelemetryEvent> events) : ITelemetrySink
    {
        public void Publish(TelemetryEvent telemetryEvent) => events.Add(telemetryEvent);
    }
}
