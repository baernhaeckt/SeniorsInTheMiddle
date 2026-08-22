using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Services;
using SeniorsInTheMiddle.Proxy.Services.Pii;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins the contract with services/pii_service: snake_case JSON in, the empty-object
/// "nothing found" reply, and one independently configured socket per service.
/// </summary>
[TestClass]
public class PythonServicesTests
{
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
}
