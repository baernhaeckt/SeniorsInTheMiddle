using System.Net;

namespace Backend.Tests.Integration;

/// <summary>
/// Pins the deny-by-default posture and, more importantly, the short list of endpoints that
/// opt out of it.
///
/// Each of these is public for a reason that is easy to forget and expensive to break: a
/// device has to fetch the CA and the PAC file before anyone can reach a login screen through
/// the proxy at all, and the platform's health probe has no credentials to offer.
/// </summary>
[TestClass]
public class AuthorizationDefaultsTests
{
    private CustomWebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup() => _factory = new CustomWebApplicationFactory<Program>();

    [TestCleanup]
    public void Cleanup() => _factory?.Dispose();

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("/health", HttpStatusCode.NoContent)]
    [DataRow("/ca.crt", HttpStatusCode.OK)]
    [DataRow("/proxy.pac", HttpStatusCode.OK)]
    public async Task BootstrapEndpointsStayAnonymous(string path, HttpStatusCode expected)
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            path, TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(expected, response.StatusCode);
    }

    [TestMethod]
    public async Task TheOpenApiDocumentStaysAnonymous()
    {
        // Swagger UI has to read this before it can offer the login that would authorize it.
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            "/openapi/v1.json", TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AProtectedEndpointIsRefusedWithoutAToken()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/auth/me", TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
