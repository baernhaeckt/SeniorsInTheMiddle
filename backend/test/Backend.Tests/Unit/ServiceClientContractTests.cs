using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Services;
using SeniorsInTheMiddle.Proxy.Services.Pii;
using SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins what the typed clients put on the wire, not just what they read back from it.
///
/// The two halves of this contract are maintained in different languages and neither compiler
/// sees the other. The python side reads snake_case keys off the payload; a C# rename or a
/// forgotten <c>pii_type =</c> produces a call that is well-formed, is answered, and detects
/// nothing -- traffic goes on flowing with the personal data still in it. Deserializing the
/// reply correctly, which is what the shape tests cover, does not catch that.
/// </summary>
[TestClass]
public class ServiceClientContractTests
{
    /// <summary>How long a call may take before the test calls it a hang.</summary>
    private static readonly TimeSpan CallBound = TimeSpan.FromSeconds(30);

    private static ServiceConnections Connect(StubPythonService service, string name)
        => new(
            ServiceOptions.From(new ConfigurationBuilder()
                .AddInMemoryCollection([
                    new KeyValuePair<string, string?>($"Services:{name}:SocketPath", service.SocketPath),
                ])
                .Build()),
            NullLoggerFactory.Instance);

    [TestMethod]
    public async Task Analyze_Sends_The_Text_And_Reads_The_Detections_Back()
    {
        await using StubPythonService service = StubPythonService.Start();
        service.Results["analyze"] = """
            {
              "detection_results": [
                {
                  "information_type": "Name",
                  "entity_type": "PERSON",
                  "score": 0.85,
                  "start_position": 6,
                  "end_position": 17,
                  "detected_text": "Hans Muster",
                  "risk_level": 2,
                  "hipaa_category": "PHI"
                }
              ],
              "detection_count": 1
            }
            """;

        await using ServiceConnections connections = Connect(service, ServiceConnections.PiiService);
        IPiiServiceClient client = new PiiServiceClient(connections);

        Assert.IsTrue(client.IsEnabled);

        PiiAnalyzeResult result = await client.AnalyzeAsync("Grüezi Hans Muster").WaitAsync(CallBound);

        Assert.IsTrue(result.HasDetections);
        Assert.AreEqual("Hans Muster", result.DetectionResults.Single().DetectedText);

        ServiceRequest sent = service.Requests.Single(r => r.Method == "analyze");
        Assert.Contains("\"text\":\"Gr\\u00FCezi Hans Muster\"", sent.Payload);
    }

    /// <summary>
    /// The detector's "nothing found" is a bare <c>{}</c>, not a zero-count result. Read as a
    /// result object it deserializes to nulls, and the first property access throws on a body
    /// that simply had no personal data in it -- which is most of them.
    /// </summary>
    [TestMethod]
    public async Task An_Empty_Object_Is_Read_As_Nothing_Found()
    {
        await using StubPythonService service = StubPythonService.Start();
        service.Results["analyze"] = "{}";

        await using ServiceConnections connections = Connect(service, ServiceConnections.PiiService);
        IPiiServiceClient client = new PiiServiceClient(connections);

        PiiAnalyzeResult result = await client.AnalyzeAsync("nothing here").WaitAsync(CallBound);

        Assert.IsFalse(result.HasDetections);
        Assert.IsEmpty(result.DetectionResults);
    }

    /// <summary>
    /// The key the python side reads is <c>pii_type</c>. Serialized with the web defaults a
    /// C# property would go out as "piiType" and the faker would be asked for a type it does
    /// not know, so every stand-in would come back the same or empty.
    /// </summary>
    [TestMethod]
    public async Task ReplacementText_Asks_By_Snake_Case_Pii_Type()
    {
        await using StubPythonService service = StubPythonService.Start();
        service.Results["replacement_text"] = "\"René Bauer\"";

        await using ServiceConnections connections = Connect(service, ServiceConnections.PiiService);
        IPiiServiceClient client = new PiiServiceClient(connections);

        string replacement = await client.ReplacementTextAsync("PERSON").WaitAsync(CallBound);

        Assert.AreEqual("René Bauer", replacement);

        ServiceRequest sent = service.Requests.Single(r => r.Method == "replacement_text");
        Assert.Contains("\"pii_type\":\"PERSON\"", sent.Payload);
    }

    /// <summary>
    /// A result that is not a string means the faker had nothing for that type. Empty is the
    /// answer the caller can act on; anything else would put a JSON fragment into a body.
    /// </summary>
    [TestMethod]
    public async Task A_Non_String_Replacement_Reads_As_Empty()
    {
        await using StubPythonService service = StubPythonService.Start();
        service.Results["replacement_text"] = "null";

        await using ServiceConnections connections = Connect(service, ServiceConnections.PiiService);
        IPiiServiceClient client = new PiiServiceClient(connections);

        Assert.AreEqual(string.Empty, await client.ReplacementTextAsync("PERSON").WaitAsync(CallBound));
    }

    /// <summary>Empty input is a caller's bug, not a service round trip; it is refused before
    /// a socket is touched.</summary>
    [TestMethod]
    public async Task Empty_Input_Is_Refused_Without_Calling_The_Service()
    {
        await using StubPythonService service = StubPythonService.Start();
        await using ServiceConnections connections = Connect(service, ServiceConnections.PiiService);
        IPiiServiceClient client = new PiiServiceClient(connections);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.AnalyzeAsync(string.Empty));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => client.ReplacementTextAsync(string.Empty));

        Assert.IsEmpty(service.Requests);
    }

    /// <summary>The privacy check reads <c>replaced_names</c>; the same rename risk as above.</summary>
    [TestMethod]
    public async Task RiskCheck_Sends_The_Replaced_Names_In_Snake_Case()
    {
        await using StubPythonService service = StubPythonService.Start();
        service.Results["risk_check"] = """{"risks":[{"name":"Hans Muster","risk_probability":0.73}]}""";

        await using ServiceConnections connections = Connect(service, ServiceConnections.PrivacyCheckService);
        IPrivacyCheckServiceClient client = new PrivacyCheckServiceClient(connections);

        Assert.IsTrue(client.IsEnabled);

        PrivacyRiskResult result = await client
            .RiskCheckAsync("Hoi [PERSON_1] aus Bern", ["Hans Muster"])
            .WaitAsync(CallBound);

        Assert.IsTrue(result.HasRisks);
        Assert.AreEqual(0.73, result.MaxRiskProbability, 0.0001);

        ServiceRequest sent = service.Requests.Single(r => r.Method == "risk_check");
        Assert.Contains("\"replaced_names\":[\"Hans Muster\"]", sent.Payload);
    }

    /// <summary>
    /// Nothing was replaced, so there is nothing to score. The sampler takes seconds per call;
    /// spending them to be told about an empty list would delay every gauge behind it.
    /// </summary>
    [TestMethod]
    public async Task RiskCheck_With_No_Names_Does_Not_Reach_The_Service()
    {
        await using StubPythonService service = StubPythonService.Start();
        await using ServiceConnections connections = Connect(service, ServiceConnections.PrivacyCheckService);
        IPrivacyCheckServiceClient client = new PrivacyCheckServiceClient(connections);

        PrivacyRiskResult result = await client.RiskCheckAsync("nothing was replaced", []).WaitAsync(CallBound);

        Assert.IsFalse(result.HasRisks);
        Assert.IsEmpty(service.Requests);
    }

    /// <summary>The startup probe's whole job is to put the service's own answer in the
    /// container log, so a wrong socket path shows up before the first request rather than
    /// as a body that quietly went uninspected.</summary>
    [TestMethod]
    public async Task The_Startup_Probe_Asks_Every_Configured_Service_For_Its_Info()
    {
        await using StubPythonService service = StubPythonService.Start();
        await using ServiceConnections connections = Connect(service, ServiceConnections.PiiService);

        ServiceStartupProbe probe = new(connections, NullLogger<ServiceStartupProbe>.Instance);
        await probe.StartAsync(CancellationToken.None);
        await probe.ExecuteTask!.WaitAsync(CallBound);

        Assert.AreEqual("$info", service.Requests.Single().Method);
    }

    /// <summary>
    /// A service that is not answering must not stop the probe: the proxy has to come up
    /// regardless, and the health check is what keeps reporting the real state.
    /// </summary>
    [TestMethod]
    public async Task The_Startup_Probe_Survives_A_Service_That_Is_Not_There()
    {
        await using ServiceConnections connections = new(
            ServiceOptions.From(new ConfigurationBuilder()
                .AddInMemoryCollection([
                    new KeyValuePair<string, string?>("Services:Pii:SocketPath", FakeService.ShortSocketPath()),
                    new KeyValuePair<string, string?>("Services:Pii:ConnectTimeoutSeconds", "1"),
                ])
                .Build()),
            NullLoggerFactory.Instance);

        ServiceStartupProbe probe = new(connections, NullLogger<ServiceStartupProbe>.Instance);
        await probe.StartAsync(CancellationToken.None);

        await probe.ExecuteTask!.WaitAsync(CallBound);

        Assert.IsTrue(probe.ExecuteTask.IsCompletedSuccessfully);
    }
}
