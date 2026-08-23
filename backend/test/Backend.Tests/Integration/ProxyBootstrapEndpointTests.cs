using Microsoft.AspNetCore.Mvc.Testing;

using System.Net;

namespace Backend.Tests.Integration;

/// <summary>
/// The two endpoints a device reads before it trusts the proxy. The PAC file is what most
/// clients are configured with, so the ports it advertises have to be the proxy listeners
/// and never the API port.
/// </summary>
[TestClass]
public class ProxyBootstrapEndpointTests
{
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup() => _factory = new WebApplicationFactory<Program>();

    [TestCleanup]
    public void Cleanup() => _factory?.Dispose();

    /// <summary>
    /// A PAC return value is a semicolon-separated list tried left to right, so the TLS
    /// proxy comes first and the plain port is the fallback for a client that cannot use it.
    /// Ports are the Development ones from appsettings.Development.json.
    /// </summary>
    [TestMethod]
    public async Task Pac_File_Advertises_Both_Proxy_Listeners()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/proxy.pac");
        string body = await response.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/x-ns-proxy-autoconfig", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("return \"HTTPS localhost:3127; PROXY localhost:3128\";", body);
        Assert.DoesNotContain("5284", body, "The PAC file must never point a device at the API port.");
    }

    [TestMethod]
    public async Task Ca_Certificate_Is_Downloadable()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/ca.crt");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/x-x509-ca-cert", response.Content.Headers.ContentType?.MediaType);
        Assert.IsGreaterThan(0, (await response.Content.ReadAsByteArrayAsync()).Length);
    }
}
