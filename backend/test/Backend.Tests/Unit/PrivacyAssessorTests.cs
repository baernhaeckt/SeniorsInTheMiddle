using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Services;
using SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace Backend.Tests.Unit;

/// <summary>
/// The privacy check is slow and optional, and the dashboard waits for its verdict. So every
/// scheduled exchange gets exactly one privacy.assessed, whatever happened to the check.
/// </summary>
[TestClass]
public class PrivacyAssessorTests
{
    private static readonly DetectedEntity Hans = new("e1", "PERSON", "Hans Meier", "[PERSON_1]", 0, 10, 0.9);
    private static readonly DetectedEntity Iban = new("e2", "IBAN_CODE", "CH93 0076 2011 6238 5295 7", "[IBAN_1]", 20, 46, 0.99);

    [TestMethod]
    public void A_Disabled_Service_Is_Skipped_At_Once()
    {
        (PrivacyAssessor assessor, List<TelemetryEvent> events) = Assessor(new StubClient(enabled: false));

        assessor.Schedule("x-1", "redacted", [Hans]);

        PrivacyAssessed assessed = events.OfType<PrivacyAssessed>().Single();
        Assert.AreEqual(PrivacyStatus.Skipped, assessed.Status);
        Assert.AreEqual("privacy check disabled", assessed.Reason);
        Assert.AreEqual("x-1", assessed.ExchangeId);
    }

    [TestMethod]
    public void Nothing_To_Check_Without_A_Person()
    {
        StubClient client = new(enabled: true);
        (PrivacyAssessor assessor, List<TelemetryEvent> events) = Assessor(client);

        assessor.Schedule("x-1", "redacted", [Iban]);

        Assert.AreEqual(PrivacyStatus.Skipped, events.OfType<PrivacyAssessed>().Single().Status);
        Assert.AreEqual(0, client.Calls);
    }

    [TestMethod]
    public async Task The_Answer_Is_Reported_By_Token_Not_By_Name()
    {
        StubClient client = new(enabled: true)
        {
            Result = new PrivacyRiskResult
            {
                Risks = [new PrivacyRisk { Name = "Hans Meier", RiskProbability = 0.73 }],
            },
        };
        (PrivacyAssessor assessor, List<TelemetryEvent> events) = Assessor(client);

        assessor.Schedule("x-1", "Hoi [PERSON_1]", [Hans, Iban]);
        PrivacyAssessed assessed = await WaitForAsync(events);

        Assert.AreEqual(PrivacyStatus.Ok, assessed.Status);
        Assert.IsNull(assessed.Reason);
        CollectionAssert.AreEqual(new[] { "Hans Meier" }, client.Names, "Only the names go to the service.");
        Assert.AreEqual("Hoi [PERSON_1]", client.Text);

        PrivacyRiskEntry risk = assessed.Risks.Single();
        Assert.AreEqual("[PERSON_1]", risk.Token);
        Assert.AreEqual(0.73, risk.Probability);
        Assert.AreEqual(0.73, assessed.MaxProbability);
        Assert.IsTrue(assessed.AssessedMs >= 0);
    }

    [TestMethod]
    public async Task A_Failing_Service_Is_Reported_As_Failed()
    {
        StubClient client = new(enabled: true) { Throws = new ServiceCallException("internal_error", "boom") };
        (PrivacyAssessor assessor, List<TelemetryEvent> events) = Assessor(client);

        assessor.Schedule("x-1", "Hoi [PERSON_1]", [Hans]);
        PrivacyAssessed assessed = await WaitForAsync(events);

        Assert.AreEqual(PrivacyStatus.Failed, assessed.Status);
        StringAssert.Contains(assessed.Reason, "boom");
        Assert.IsTrue(events.OfType<ProxyLog>().Any(log => log.Level == TelemetryLogLevel.Warn && log.ExchangeId == "x-1"));
    }

    [TestMethod]
    public async Task A_Second_Exchange_Is_Skipped_While_One_Is_Running()
    {
        StubClient client = new(enabled: true) { Gate = new TaskCompletionSource() };
        (PrivacyAssessor assessor, List<TelemetryEvent> events) = Assessor(client);

        assessor.Schedule("x-1", "Hoi [PERSON_1]", [Hans]);
        await client.Started.Task;
        assessor.Schedule("x-2", "Hoi [PERSON_1]", [Hans]);

        PrivacyAssessed second = events.OfType<PrivacyAssessed>().Single(e => e.ExchangeId == "x-2");
        Assert.AreEqual(PrivacyStatus.Skipped, second.Status);
        Assert.AreEqual("assessor busy", second.Reason);

        client.Gate.SetResult();
        PrivacyAssessed first = await WaitForAsync(events, "x-1");
        Assert.AreEqual(PrivacyStatus.Ok, first.Status);
    }

    private static (PrivacyAssessor, List<TelemetryEvent>) Assessor(StubClient client)
    {
        List<TelemetryEvent> events = [];

        return (
            new PrivacyAssessor(client, new Sink(events), new Lifetime(), NullLogger<PrivacyAssessor>.Instance),
            events);
    }

    private static async Task<PrivacyAssessed> WaitForAsync(List<TelemetryEvent> events, string exchangeId = "x-1")
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            PrivacyAssessed? assessed;
            lock (events)
                assessed = events.OfType<PrivacyAssessed>().FirstOrDefault(e => e.ExchangeId == exchangeId);

            if (assessed is not null)
                return assessed;

            await Task.Delay(25);
        }

        throw new AssertFailedException("No privacy.assessed arrived.");
    }

    private sealed class Sink(List<TelemetryEvent> events) : ITelemetrySink
    {
        public void Publish(TelemetryEvent telemetryEvent)
        {
            lock (events)
                events.Add(telemetryEvent);
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
    }

    private sealed class StubClient(bool enabled) : IPrivacyCheckServiceClient
    {
        public bool IsEnabled => enabled;

        public PrivacyRiskResult Result { get; init; } = PrivacyRiskResult.Empty;

        public Exception? Throws { get; init; }

        public TaskCompletionSource? Gate { get; init; }

        public TaskCompletionSource Started { get; } = new();

        public int Calls { get; private set; }

        public string? Text { get; private set; }

        public string[] Names { get; private set; } = [];

        public async Task<PrivacyRiskResult> RiskCheckAsync(string text, IReadOnlyList<string> replacedNames, CancellationToken cancellationToken = default)
        {
            Calls++;
            Text = text;
            Names = [.. replacedNames];
            Started.TrySetResult();

            if (Gate is not null)
                await Gate.Task;

            if (Throws is not null)
                throw Throws;

            return Result;
        }
    }
}
