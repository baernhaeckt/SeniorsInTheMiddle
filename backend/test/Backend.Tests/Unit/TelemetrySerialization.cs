using System.Text.Json;

using SeniorsInTheMiddle.Proxy.Telemetry;

namespace Backend.Tests.Unit;

/// <summary>
/// The dashboard validates every frame against a valibot schema and silently drops what
/// does not match, so a shape mistake here shows up as an empty dashboard rather than an
/// error. These assertions are the same contract, written out.
/// </summary>
[TestClass]
public class TelemetrySerialization
{
    [TestMethod]
    public void RequestObserved_MatchesTheProtocolShape()
    {
        string json = TelemetryJson.Serialize(new RequestObserved(
            RequestId: "r-00001",
            At: 1_700_000_000_000,
            ClientIp: "10.0.0.17",
            ClientLabel: "Tablet · .17",
            Method: "GET",
            Scheme: TelemetryScheme.Https,
            Host: "cdn.example.ch",
            Path: "/app.css",
            ContentType: "text/css",
            RequestBytes: 0,
            Treatment: Treatment.Passthrough,
            Reason: "text/css"));

        Assert.AreEqual(
            """
            {"type":"request.observed","requestId":"r-00001","at":1700000000000,"clientIp":"10.0.0.17","clientLabel":"Tablet · .17","method":"GET","scheme":"https","host":"cdn.example.ch","path":"/app.css","contentType":"text/css","requestBytes":0,"treatment":"passthrough","reason":"text/css"}
            """,
            json);
    }

    [TestMethod]
    public void OptionalFields_AreOmittedRatherThanNull()
    {
        // valibot's v.optional accepts a missing key, not an explicit null.
        string json = TelemetryJson.Serialize(new RequestObserved(
            "r-1", 1, "127.0.0.1", "Device · .1", "POST", TelemetryScheme.Http,
            "receiver", "/api/v1/forms/intake", ContentType: null, RequestBytes: 12,
            Treatment: Treatment.Clean, Reason: "no identifiers", ExchangeId: null));

        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("contentType"));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("exchangeId"));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("null"));
    }

    [TestMethod]
    public void EveryEventCarriesItsDiscriminator()
    {
        (TelemetryEvent Event, string Type)[] cases =
        [
            (new ServerHello(3, new ProxyInfo("p", "r", "intercept", "rewrite"), Policy()), "hello"),
            (new RequestObserved("r", 1, "ip", "l", "GET", TelemetryScheme.Http, "h", "/", null, 0,
                Treatment.Passthrough, "no body"), "request.observed"),
            (new RequestCompleted("r", 1, 200, 10, 1.5), "request.completed"),
            (new ExchangeOpened("x", "r", 1, "l", "POST", TelemetryScheme.Https, "h", "/", "application/json", "{}"),
                "exchange.opened"),
            (new DetectionCompleted("x", 1, [], 2.0, null, new Dictionary<string, int>(), 0, []), "detection.completed"),
            (new RedactionCompleted("x", 1, "{}"), "redaction.completed"),
            (new UpstreamDispatched("x", 1, "https://h/", 2), "upstream.dispatched"),
            (new UpstreamResponded("x", 1, 200, "{}", 3.0), "upstream.responded"),
            (new RehydrationCompleted("x", 1, "{}", 0), "rehydration.completed"),
            (new ExchangeDelivered("x", 1, 4.0, ExchangeTiming.None), "exchange.delivered"),
            (new PrivacyAssessed("x", 1, [new PrivacyRiskEntry("[P]", 0.5)], 0.5, 900, PrivacyStatus.Ok), "privacy.assessed"),
            (new ProxyLog(1, TelemetryLogLevel.Block, "m"), "log"),
        ];

        foreach ((TelemetryEvent telemetryEvent, string expected) in cases)
        {
            using JsonDocument document = JsonDocument.Parse(TelemetryJson.Serialize(telemetryEvent));

            Assert.IsTrue(
                document.RootElement.TryGetProperty("type", out JsonElement type),
                $"{expected} was serialized without a type discriminator.");
            Assert.AreEqual(expected, type.GetString());
        }
    }

    [TestMethod]
    public void Hello_AnnouncesTheVersionTheDashboardExpects()
    {
        Assert.AreEqual(3, TelemetryJson.ProtocolVersion);

        string json = TelemetryJson.Serialize(
            new ServerHello(TelemetryJson.ProtocolVersion, new ProxyInfo("Proxy", "local", "intercept", "rewrite"), Policy()));

        Assert.AreEqual(
            """
            {"type":"hello","version":3,"proxy":{"name":"Proxy","region":"local","mode":"intercept","policy":"rewrite"},"policy":{"rewrite":true,"bypassHosts":["challenges.cloudflare.com"],"inspectOnly":{"chatgpt.com":["/backend-api/conversation"]},"maxBodyBytes":1048576,"confidenceThreshold":0.6,"services":{"pii":"ok","privacyCheck":"disabled"}}}
            """,
            json);
    }

    [TestMethod]
    public void PrivacyAssessed_OmitsTheReasonWhenThereIsNone()
    {
        string ok = TelemetryJson.Serialize(new PrivacyAssessed("x", 1, [], 0, 2.5, PrivacyStatus.Ok));
        string skipped = TelemetryJson.Serialize(new PrivacyAssessed("x", 1, [], 0, 0, PrivacyStatus.Skipped, "no names"));

        StringAssert.Contains(ok, "\"status\":\"ok\"");
        StringAssert.DoesNotMatch(ok, new System.Text.RegularExpressions.Regex("reason"));
        StringAssert.Contains(skipped, "\"status\":\"skipped\",\"reason\":\"no names\"");
    }

    [TestMethod]
    public void DetectionCompleted_CarriesTheAggregatesAndTheNearMisses()
    {
        string json = TelemetryJson.Serialize(new DetectionCompleted(
            "x-1",
            1,
            [new DetectedEntity("e1", "PERSON", "Hans", "[P]", 0, 4, 0.9, "Full Name", 3, "Not Protected Health Information")],
            3.5,
            0.9,
            new Dictionary<string, int> { ["PERSON"] = 1 },
            2,
            [new NearMiss("LOCATION", "Bern", 0.4)]));

        StringAssert.Contains(json, "\"informationType\":\"Full Name\",\"riskLevel\":3,\"hipaaCategory\":\"Not Protected Health Information\"");
        StringAssert.Contains(json, "\"riskScoreMean\":0.9,\"typeFrequencies\":{\"PERSON\":1},\"suppressed\":2,\"nearMisses\":[{\"kind\":\"LOCATION\",\"value\":\"Bern\",\"confidence\":0.4}]");
    }

    private static ProxyPolicy Policy() => new(
        true,
        ["challenges.cloudflare.com"],
        new Dictionary<string, string[]> { ["chatgpt.com"] = ["/backend-api/conversation"] },
        1024 * 1024,
        0.6,
        new ServiceStates(ServiceState.Ok, ServiceState.Disabled));

    [TestMethod]
    public void Entities_UseTheProtocolsUppercaseKinds()
    {
        string json = TelemetryJson.Serialize(new DetectionCompleted(
            "x-1",
            1,
            [new DetectedEntity("e1", "AHV", "756.1234.5678.97", "[AHV_1]", 9, 24, 0.97)],
            3.5,
            0.97,
            new Dictionary<string, int> { ["AHV"] = 1 },
            0,
            []));

        StringAssert.Contains(json, "\"kind\":\"AHV\"");
        StringAssert.Contains(json, "\"confidence\":0.97");
    }
}
