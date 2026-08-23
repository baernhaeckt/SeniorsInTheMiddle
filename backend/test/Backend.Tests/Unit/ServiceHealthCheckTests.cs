using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Services;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins what /healthz reports, which is what a container platform restarts the process on.
///
/// The distinction that matters is between a service that is off and one that is broken. A
/// dev box has no unix sockets and therefore no services, and a health check that called that
/// Unhealthy would put every developer's container in a restart loop. A deployed container
/// whose PII daemon died has the same shape of "no answer" and must be caught.
/// </summary>
[TestClass]
public class ServiceHealthCheckTests
{
    /// <summary>How long a check may take before the test calls it a hang.</summary>
    private static readonly TimeSpan CheckBound = TimeSpan.FromSeconds(30);

    private static readonly HealthCheckContext Context = new()
    {
        Registration = new HealthCheckRegistration("services", _ => null!, HealthStatus.Unhealthy, null),
    };

    private static ServiceConnections Connections(params (string Key, string Value)[] settings)
        => new(
            ServiceOptions.From(new ConfigurationBuilder()
                .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
                .Build()),
            NullLoggerFactory.Instance);

    /// <summary>The Windows dev box: nothing configured, so nothing can be unreachable.</summary>
    [TestMethod]
    public async Task No_Service_Configured_Is_Healthy_And_Says_Disabled()
    {
        await using ServiceConnections connections = Connections();

        HealthCheckResult result = await new ServiceHealthCheck(connections)
            .CheckHealthAsync(Context)
            .WaitAsync(CheckBound);

        Assert.AreEqual(HealthStatus.Healthy, result.Status);
        Assert.AreEqual("disabled", result.Data[ServiceConnections.PiiService]);
        Assert.AreEqual("disabled", result.Data[ServiceConnections.PrivacyCheckService]);
    }

    /// <summary>Every known service is named whether or not it is configured, so the output
    /// says what this build can talk to rather than what happens to be switched on.</summary>
    [TestMethod]
    public async Task Every_Known_Service_Is_Named_In_The_Data()
    {
        await using ServiceConnections connections = Connections();

        HealthCheckResult result = await new ServiceHealthCheck(connections)
            .CheckHealthAsync(Context)
            .WaitAsync(CheckBound);

        foreach (string known in ServiceConnections.KnownServices)
            Assert.IsTrue(result.Data.ContainsKey(known), $"{known} is missing from the health data.");
    }

    [TestMethod]
    public async Task A_Service_That_Answers_Is_Healthy_And_Names_Its_Socket()
    {
        await using StubPythonService service = StubPythonService.Start();
        await using ServiceConnections connections = Connections(
            ("Services:Pii:SocketPath", service.SocketPath));

        HealthCheckResult result = await new ServiceHealthCheck(connections)
            .CheckHealthAsync(Context)
            .WaitAsync(CheckBound);

        Assert.AreEqual(HealthStatus.Healthy, result.Status);
        Assert.AreEqual($"ok ({service.SocketPath})", result.Data[ServiceConnections.PiiService]);

        // The one that is off does not drag the answering one down, and vice versa.
        Assert.AreEqual("disabled", result.Data[ServiceConnections.PrivacyCheckService]);
    }

    /// <summary>
    /// A path that is configured but has nothing listening on it: the daemon failed to start,
    /// or the socket path and the supervisord program drifted apart.
    /// </summary>
    [TestMethod]
    public async Task A_Configured_Service_With_No_Socket_Is_Unhealthy()
    {
        string missing = FakeService.ShortSocketPath();
        await using ServiceConnections connections = Connections(
            ("Services:Pii:SocketPath", missing),
            // Without this the connect keeps retrying for its full 30s default and the check
            // times out on its own 5s ping bound instead, which is the same verdict by luck.
            ("Services:Pii:ConnectTimeoutSeconds", "1"));

        HealthCheckResult result = await new ServiceHealthCheck(connections)
            .CheckHealthAsync(Context)
            .WaitAsync(CheckBound);

        Assert.AreEqual(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(ServiceConnections.PiiService, result.Description!);
        Assert.Contains("unreachable", (string)result.Data[ServiceConnections.PiiService]);
    }

    /// <summary>
    /// One broken service is enough to fail the check, and the description names which,
    /// because "Unhealthy" alone sends whoever reads it to the wrong daemon's log.
    /// </summary>
    [TestMethod]
    public async Task One_Broken_Service_Fails_The_Check_And_Is_Named()
    {
        await using StubPythonService healthy = StubPythonService.Start();
        string missing = FakeService.ShortSocketPath();

        await using ServiceConnections connections = Connections(
            ("Services:Pii:SocketPath", healthy.SocketPath),
            ("Services:PrivacyCheck:SocketPath", missing),
            ("Services:PrivacyCheck:ConnectTimeoutSeconds", "1"));

        HealthCheckResult result = await new ServiceHealthCheck(connections)
            .CheckHealthAsync(Context)
            .WaitAsync(CheckBound);

        Assert.AreEqual(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(ServiceConnections.PrivacyCheckService, result.Description!);
        Assert.DoesNotContain(ServiceConnections.PiiService, result.Description!);
        Assert.AreEqual($"ok ({healthy.SocketPath})", result.Data[ServiceConnections.PiiService]);
    }

    /// <summary>
    /// A service that accepts the connection but never answers is unreachable too. This is the
    /// case a plain "can I connect?" check would call healthy: the daemon is up, its model
    /// failed to load, and it will never reply.
    /// </summary>
    [TestMethod]
    public async Task A_Service_That_Never_Answers_Is_Unhealthy()
    {
        await using StubPythonService stalled = StubPythonService.StartSlow(TimeSpan.FromMinutes(5));
        await using ServiceConnections connections = Connections(
            ("Services:Pii:SocketPath", stalled.SocketPath));

        HealthCheckResult result = await new ServiceHealthCheck(connections)
            .CheckHealthAsync(Context)
            .WaitAsync(CheckBound);

        Assert.AreEqual(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("unreachable", (string)result.Data[ServiceConnections.PiiService]);
    }
}
