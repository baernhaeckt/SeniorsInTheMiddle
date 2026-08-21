using SeniorsInTheMiddle.Proxy.Auth.Api;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Backend.Tests.Integration;

[TestClass]
public class AuthEndpointTests
{
    private CustomWebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup() => _factory = new CustomWebApplicationFactory<Program>();

    [TestCleanup]
    public void Cleanup() => _factory?.Dispose();

    [TestMethod]
    public async Task AuthRegisterEndpoint_ShouldReturn200Ok_WhenFreshHousehold()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest("testuser", "tester@test.ch", "Test123"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AuthRegisterEndpoint_ShouldReturn200Ok_WhenExistingHousehold()
    {
        HttpClient client = _factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest("testuser", "tester@test.ch", "Test123"));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task AuthLoginEndpoint_ShouldReturn200OkAndToken()
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response1 = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest("testuser", "tester@test.ch", "Test123"));
        response1.EnsureSuccessStatusCode();

        HttpResponseMessage response2 = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new RegisterRequest("testuser", "tester@test.ch", "Test123"));

        string token = await response2.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response2.StatusCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(token));
    }

    [TestMethod]
    public async Task AuthMeEndpoint_ShouldReturn200OkAndUser()
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage response1 = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest("testuser", "tester@test.ch", "Test123"));
        response1.EnsureSuccessStatusCode();

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/login",
            new RegisterRequest("testuser", "tester@test.ch", "Test123"));
        LoginResponse? loginResponse = await response.Content.ReadFromJsonAsync<LoginResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", loginResponse?.Token);

        HttpResponseMessage meResponse = await client.GetAsync("/api/v1/auth/me");

        Assert.AreEqual(HttpStatusCode.OK, meResponse.StatusCode);
        ProfileResponse? responseContent = await meResponse.Content.ReadFromJsonAsync<ProfileResponse>();
        Assert.IsNotNull(responseContent?.Email);
    }
}
