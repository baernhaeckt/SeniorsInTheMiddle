using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Services;
using SeniorsInTheMiddle.Proxy.Services.Pii;
using SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins the contract with services/pii_service: snake_case JSON in, the empty-object
/// "nothing found" reply, and one independently configured socket per service.
/// </summary>
[TestClass]
public class PythonServicesTests
{
    /// <summary>How long a call may take before the test calls it a hang.</summary>
    private static readonly TimeSpan CallBound = TimeSpan.FromSeconds(15);

    private static IConfiguration Config(params (string Key, string Value)[] settings)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();

    [TestMethod]
    public void AnalyzeResult_Reads_The_Python_Snake_Case_Shape()
    {
        const string json = """
            {
              "detection_results": [
                {
                  "information_type": "Name",
                  "entity_type": "PERSON",
                  "score": 0.85,
                  "start_position": 0,
                  "end_position": 11,
                  "detected_text": "Hans Muster",
                  "risk_level": 2,
                  "hipaa_category": "PHI"
                }
              ],
              "detection_count": 1,
              "risk_score_mean": 0.85,
              "risk_score_median": 0.85,
              "detected_pii_types": ["PERSON"],
              "detected_pii_type_frequencies": {"PERSON": 1}
            }
            """;

        PiiAnalyzeResult result = JsonSerializer.Deserialize<PiiAnalyzeResult>(json, PiiJson.Options)!;

        Assert.IsTrue(result.HasDetections);
        Assert.AreEqual(1, result.DetectionCount);
        Assert.AreEqual(0.85, result.RiskScoreMean, 0.0001);
        PiiDetection detection = result.DetectionResults.Single();
        Assert.AreEqual("PERSON", detection.EntityType);
        Assert.AreEqual("Hans Muster", detection.DetectedText);
        Assert.AreEqual(11, detection.EndPosition);
        Assert.AreEqual("PHI", detection.HipaaCategory);
        Assert.AreEqual(1, result.DetectedPiiTypeFrequencies["PERSON"]);
    }

    [TestMethod]
    public void Ignored_Results_Are_Read_Even_When_Nothing_Was_Detected()
    {
        const string json = """
            {
              "ignored_results": [
                {
                  "information_type": "Location",
                  "entity_type": "LOCATION",
                  "score": 0.4,
                  "start_position": 0,
                  "end_position": 4,
                  "detected_text": "Bern",
                  "risk_level": 2,
                  "hipaa_category": "NON_PHI"
                }
              ]
            }
            """;

        PiiAnalyzeResult result = JsonSerializer.Deserialize<PiiAnalyzeResult>(json, PiiJson.Options)!;

        Assert.IsFalse(result.HasDetections);
        PiiDetection ignored = result.IgnoredResults.Single();
        Assert.AreEqual("LOCATION", ignored.EntityType);
        Assert.AreEqual(0.4, ignored.Score, 0.0001);
    }

    [TestMethod]
    public void Empty_Object_Is_The_No_Detections_Result()
    {
        PiiAnalyzeResult result = JsonSerializer.Deserialize<PiiAnalyzeResult>("{}", PiiJson.Options)!;

        Assert.IsFalse(result.HasDetections);
        Assert.IsEmpty(result.DetectionResults);
    }

    [TestMethod]
    public void Each_Service_Has_Its_Own_Socket()
    {
        ServiceOptions options = ServiceOptions.From(Config(
            ("Services:Pii:SocketPath", "/run/services/pii-service.sock"),
            ("Services:Summary:SocketPath", "/run/services/summary-service.sock"),
            ("Services:Summary:ConnectTimeoutSeconds", "5")));

        Assert.AreEqual("/run/services/pii-service.sock", options.Get("Pii").SocketPath);
        Assert.AreEqual(30, options.Get("Pii").ConnectTimeoutSeconds);
        Assert.AreEqual("/run/services/summary-service.sock", options.Get("summary").SocketPath);
        Assert.AreEqual(5, options.Get("Summary").ConnectTimeoutSeconds);
        Assert.IsFalse(options.Get("Unknown").IsConfigured);
    }

    [TestMethod]
    public async Task Unconfigured_Service_Is_Disabled_Not_Broken()
    {
        await using ServiceConnections connections = new(ServiceOptions.From(Config()), NullLoggerFactory.Instance);
        IPiiServiceClient client = new PiiServiceClient(connections);

        Assert.IsFalse(client.IsEnabled);
        Assert.IsTrue(connections.All.Any(c => c.Name == ServiceConnections.PiiService));
        await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(() => client.AnalyzeAsync("Hans Muster"));
    }

    [TestMethod]
    public void RiskCheckResult_Reads_The_Python_Snake_Case_Shape()
    {
        const string json = """
            {
              "risks": [
                { "name": "Hans Muster", "risk_probability": 0.7321 }
              ]
            }
            """;

        PrivacyRiskResult result = JsonSerializer.Deserialize<PrivacyRiskResult>(json, PiiJson.Options)!;

        Assert.IsTrue(result.HasRisks);
        PrivacyRisk risk = result.Risks.Single();
        Assert.AreEqual("Hans Muster", risk.Name);
        Assert.AreEqual(0.7321, risk.RiskProbability, 0.0001);
        Assert.AreEqual(0.7321, result.MaxRiskProbability, 0.0001);
    }

    [TestMethod]
    public async Task Unconfigured_PrivacyCheck_Service_Is_Disabled_Not_Broken()
    {
        await using ServiceConnections connections = new(ServiceOptions.From(Config()), NullLoggerFactory.Instance);
        IPrivacyCheckServiceClient client = new PrivacyCheckServiceClient(connections);

        Assert.IsFalse(client.IsEnabled);
        Assert.IsTrue(connections.All.Any(c => c.Name == ServiceConnections.PrivacyCheckService));
        await Assert.ThrowsExactlyAsync<ServiceUnavailableException>(
            () => client.RiskCheckAsync("Hans Muster wohnt in Bern", ["Hans Muster"]));
    }

    /// <summary>
    /// A reply the read loop cannot parse kills the loop, and nothing restarts it. The socket
    /// usually stays writable, so without a fault recorded on the client the next call sends
    /// its frame quite happily and then waits for an answer nobody is left to deliver -- with
    /// no per-call timeout, that is for the life of the process. The bound on the calls below
    /// is the assertion: a hang is the regression, and it surfaces as a TimeoutException where
    /// the connection's own failure was expected.
    /// </summary>
    [TestMethod]
    public async Task A_Reply_That_Kills_The_Read_Loop_Fails_Later_Calls_Instead_Of_Hanging()
    {
        string socketPath = FakeService.ShortSocketPath();
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);

        try
        {
            Task<Socket> accepting = listener.AcceptAsync();
            await using ServiceSocketClient client = await ServiceSocketClient.ConnectAsync(socketPath);
            using Socket server = await accepting;

            // Started before the service reads, because the frame it reads is this one.
            Task<JsonElement> call = client.CallAsync("analyze");

            await FakeService.ReadFrameAsync(server);
            await FakeService.WriteFrameAsync(server, "this is not json"u8.ToArray());

            // The malformed frame reaches the caller as the parse failure it is.
            await Assert.ThrowsAsync<JsonException>(() => call.WaitAsync(CallBound));

            // And the connection is known to be finished, so the next call says so at once
            // rather than blocking on a reply that can no longer come.
            await Assert.ThrowsExactlyAsync<IOException>(
                () => client.CallAsync("analyze").WaitAsync(CallBound));
        }
        finally
        {
            File.Delete(socketPath);
        }
    }
}

/// <summary>
/// The service side of the length-prefixed JSON framing, for tests that need to answer a
/// <see cref="ServiceSocketClient"/> with something the real runtime would never send.
/// </summary>
internal static class FakeService
{
    private const int HeaderSize = 4;

    /// <summary>
    /// A unix socket path short enough to bind. The address is a fixed-size field -- 108 bytes
    /// on Linux, 108 on Windows too -- so a path under the temp directory can be too long to
    /// bind on a machine whose temp directory is nested deep.
    /// </summary>
    public static string ShortSocketPath()
        => Path.Combine(Path.GetTempPath(), $"sitm{Guid.NewGuid():N}"[..24] + ".sock");

    public static async Task<byte[]> ReadFrameAsync(Socket socket)
    {
        byte[] header = new byte[HeaderSize];
        await ReadExactlyAsync(socket, header);

        byte[] body = new byte[BinaryPrimitives.ReadUInt32BigEndian(header)];
        await ReadExactlyAsync(socket, body);

        return body;
    }

    public static async Task WriteFrameAsync(Socket socket, byte[] body)
    {
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);

        await socket.SendAsync(header, SocketFlags.None);
        await socket.SendAsync(body, SocketFlags.None);
    }

    private static async Task ReadExactlyAsync(Socket socket, byte[] destination)
    {
        int filled = 0;
        while (filled < destination.Length)
        {
            int read = await socket.ReceiveAsync(destination.AsMemory(filled), SocketFlags.None);
            if (read == 0)
                throw new EndOfStreamException("The client closed before the frame was whole.");

            filled += read;
        }
    }
}
