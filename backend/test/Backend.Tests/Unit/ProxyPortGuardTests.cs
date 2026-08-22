using SeniorsInTheMiddle.Proxy.Forwarding;

using Microsoft.AspNetCore.Http;

using System.Net;

namespace Backend.Tests.Unit;

/// <summary>
/// The port guard is what actually keeps Swagger, the WebAPI and the telemetry stream off
/// the ports devices are pointed at. Only the two bootstrap paths a client needs before it
/// trusts us survive there.
/// </summary>
[TestClass]
public class ProxyPortGuardTests
{
    private static readonly ProxyPorts Ports = new(HttpProxy: 3128, HttpsProxy: 3127, Api: 8080);

    /// <summary>Runs the guard and reports whether the request reached the rest of the pipeline.</summary>
    private static async Task<(bool ReachedPipeline, int StatusCode)> Invoke(int localPort, string path)
    {
        var reached = false;
        ProxyPortGuard guard = new(_ => { reached = true; return Task.CompletedTask; }, Ports);

        DefaultHttpContext context = new();
        context.Connection.LocalPort = localPort;
        context.Request.Path = path;

        await guard.InvokeAsync(context);

        return (reached, context.Response.StatusCode);
    }

    [TestMethod]
    [DataRow("/api/v1/auth/login")]
    [DataRow("/swagger")]
    [DataRow("/openapi/v1.json")]
    [DataRow("/hub/telemetry")]
    [DataRow("/health")]
    public async Task Api_Port_Serves_Everything(string path)
    {
        (bool reached, _) = await Invoke(Ports.Api, path);

        Assert.IsTrue(reached);
    }

    [TestMethod]
    [DataRow(3128, "/api/v1/auth/login")]
    [DataRow(3128, "/swagger")]
    [DataRow(3128, "/hub/telemetry")]
    [DataRow(3128, "/health")]
    [DataRow(3127, "/api/v1/auth/login")]
    [DataRow(3127, "/swagger")]
    public async Task Proxy_Ports_Refuse_The_Api(int localPort, string path)
    {
        (bool reached, int status) = await Invoke(localPort, path);

        Assert.IsFalse(reached);
        Assert.AreEqual((int)HttpStatusCode.NotFound, status);
    }

    [TestMethod]
    [DataRow(3128, "/ca.crt")]
    [DataRow(3128, "/proxy.pac")]
    [DataRow(3127, "/ca.crt")]
    [DataRow(3127, "/proxy.pac")]
    public async Task Proxy_Ports_Serve_What_A_Device_Needs_To_Be_Set_Up(int localPort, string path)
    {
        (bool reached, _) = await Invoke(localPort, path);

        Assert.IsTrue(reached);
    }

    /// <summary>A device typing the URL by hand should not be told the file does not exist.</summary>
    [TestMethod]
    [DataRow("/CA.CRT")]
    [DataRow("/Proxy.Pac")]
    public async Task Bootstrap_Paths_Ignore_Casing(string path)
    {
        (bool reached, _) = await Invoke(Ports.HttpProxy, path);

        Assert.IsTrue(reached);
    }

    /// <summary>An in-process test host reports port 0 and must not be guarded as a proxy port.</summary>
    [TestMethod]
    public async Task Unknown_Port_Is_Left_Alone()
    {
        (bool reached, _) = await Invoke(0, "/health");

        Assert.IsTrue(reached);
    }
}
