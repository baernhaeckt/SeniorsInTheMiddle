using System.Diagnostics;

using SeniorsInTheMiddle.Proxy.Services;
using SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;
using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Asks the privacy-check service how recoverable the replaced names still are from the
/// redacted text, and publishes the answer as <c>privacy.assessed</c>.
///
/// Entirely off the request path. The service samples an MCMC chain per call and takes
/// seconds, which no response can wait for; the answer is a fact for the dashboard, not for
/// the exchange. So <see cref="Schedule"/> returns at once, the work runs on the pool and is
/// cut short when the host stops, and whatever happens -- disabled service, no names, a
/// failure, a timeout -- the exchange still gets exactly one event saying so. A dashboard
/// that is waiting for the gauge never waits forever.
///
/// One check at a time. The sampler is CPU-bound and a chat session posts several bodies in
/// quick succession; queueing them would stack seconds behind seconds and the answers would
/// land long after the exchanges left the screen. A check that cannot start is skipped and
/// says so.
/// </summary>
sealed class PrivacyAssessor
{
    private const string PersonKind = "PERSON";

    private readonly IPrivacyCheckServiceClient client;
    private readonly ITelemetrySink sink;
    private readonly IHostApplicationLifetime lifetime;
    private readonly ILogger<PrivacyAssessor> logger;
    private readonly TimeSpan callTimeout;
    private readonly SemaphoreSlim oneAtATime = new(1, 1);

    public PrivacyAssessor(
        IPrivacyCheckServiceClient client,
        ITelemetrySink sink,
        IHostApplicationLifetime lifetime,
        ServiceOptions options,
        ILogger<PrivacyAssessor> logger)
    {
        this.client = client;
        this.sink = sink;
        this.lifetime = lifetime;
        this.logger = logger;
        callTimeout = TimeSpan.FromSeconds(options.Get(ServiceConnections.PrivacyCheckService).CallTimeoutSeconds);
    }

    /// <summary>Starts the check for one exchange. Never throws, never waits.</summary>
    public void Schedule(string exchangeId, string redactedText, IReadOnlyList<DetectedEntity> entities)
    {
        if (!client.IsEnabled)
        {
            Skip(exchangeId, "privacy check disabled");
            return;
        }

        // The token is what the dashboard knows the name by; the service answers with the
        // name. First token wins when the same name was replaced twice, which is the same
        // stand-in anyway.
        Dictionary<string, string> tokenByName = new(StringComparer.Ordinal);

        foreach (DetectedEntity entity in entities)
        {
            if (string.Equals(entity.Kind, PersonKind, StringComparison.OrdinalIgnoreCase) && entity.Value.Length > 0)
                tokenByName.TryAdd(entity.Value, entity.Token);
        }

        if (tokenByName.Count == 0)
        {
            Skip(exchangeId, "no names");
            return;
        }

        if (redactedText.Length == 0)
        {
            Skip(exchangeId, "empty body");
            return;
        }

        if (!oneAtATime.Wait(0))
        {
            Skip(exchangeId, "assessor busy");
            return;
        }

        _ = Task.Run(() => RunAsync(exchangeId, redactedText, tokenByName), CancellationToken.None);
    }

    private async Task RunAsync(string exchangeId, string redactedText, Dictionary<string, string> tokenByName)
    {
        long startedAt = Stopwatch.GetTimestamp();

        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            timeout.CancelAfter(callTimeout);

            PrivacyRiskResult result = await client.RiskCheckAsync(redactedText, [.. tokenByName.Keys], timeout.Token);

            List<PrivacyRiskEntry> risks = [];

            foreach (PrivacyRisk risk in result.Risks)
            {
                if (!tokenByName.TryGetValue(risk.Name, out string? token))
                {
                    logger.LogWarning("The privacy check answered for {Name}, which was not asked about.", risk.Name);
                    continue;
                }

                risks.Add(new PrivacyRiskEntry(token, Math.Clamp(risk.RiskProbability, 0, 1)));
            }

            sink.Publish(new PrivacyAssessed(
                exchangeId,
                TelemetryJson.Now(),
                risks,
                risks.Count == 0 ? 0 : risks.Max(risk => risk.Probability),
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                PrivacyStatus.Ok));
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            // Shutting down; nobody is listening.
        }
        catch (Exception ex) when (ex is OperationCanceledException or ServiceUnavailableException or ServiceCallException)
        {
            string reason = ex is OperationCanceledException ? $"timed out after {callTimeout.TotalSeconds:0}s" : ex.Message;

            logger.LogWarning(ex, "The privacy check for {ExchangeId} failed.", exchangeId);
            sink.Publish(new ProxyLog(TelemetryJson.Now(), TelemetryLogLevel.Warn, $"Privacy check failed: {reason}", exchangeId));
            sink.Publish(new PrivacyAssessed(
                exchangeId,
                TelemetryJson.Now(),
                [],
                0,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                PrivacyStatus.Failed,
                reason));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The privacy check for {ExchangeId} threw.", exchangeId);
            sink.Publish(new PrivacyAssessed(
                exchangeId,
                TelemetryJson.Now(),
                [],
                0,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                PrivacyStatus.Failed,
                ex.Message));
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    private void Skip(string exchangeId, string reason)
        => sink.Publish(new PrivacyAssessed(exchangeId, TelemetryJson.Now(), [], 0, 0, PrivacyStatus.Skipped, reason));
}
