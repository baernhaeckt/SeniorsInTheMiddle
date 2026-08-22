using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

using SeniorsInTheMiddle.Proxy.Auth.Api;

namespace Backend.Tests.Integration;

/// <summary>
/// The demo-account endpoint hands out a working login, so the case that matters here is the
/// one where it refuses to. Advertising is off by default and must stay off in production;
/// these tests set the flag explicitly rather than leaning on whatever Development happens to
/// configure, so a change to appsettings cannot quietly make the "off" case untested.
/// </summary>
[TestClass]
public class DemoAccountEndpointTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TheSeededCredentialsAreReturnedWhenAdvertisingIsOn()
    {
        using var factory = new SeedConfiguredFactory(advertise: true);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/auth/demo-account", TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        DemoAccountResponse? account = await response.Content
            .ReadFromJsonAsync<DemoAccountResponse>(TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(SeedConfiguredFactory.SeedUsername, account?.Username);
        Assert.AreEqual(SeedConfiguredFactory.SeedPassword, account?.Password);
    }

    [TestMethod]
    public async Task NothingIsReturnedWhenAdvertisingIsOff()
    {
        using var factory = new SeedConfiguredFactory(advertise: false);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(
            "/api/v1/auth/demo-account", TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task TheSeededAccountCanSignInEvenWhenItIsNotAdvertised()
    {
        // Advertising only controls whether the password is published. The account itself is
        // still seeded, which is the part that keeps a restarted container usable.
        using var factory = new SeedConfiguredFactory(advertise: false);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new LoginRequest(SeedConfiguredFactory.SeedUsername, SeedConfiguredFactory.SeedPassword),
            TestContext.CancellationTokenSource.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class SeedConfiguredFactory : CustomWebApplicationFactory<Program>
    {
        public const string SeedUsername = "seeded-demo";
        public const string SeedPassword = "seeded-secret";

        private readonly bool advertise;

        public SeedConfiguredFactory(bool advertise) => this.advertise = advertise;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Auth:SeedUser:Username"] = SeedUsername,
                    ["Auth:SeedUser:Email"] = "seeded@test.ch",
                    ["Auth:SeedUser:Password"] = SeedPassword,
                    ["Auth:SeedUser:Advertise"] = advertise ? "true" : "false",
                });
            });
        }
    }
}
