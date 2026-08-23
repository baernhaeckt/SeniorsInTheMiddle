using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using SeniorsInTheMiddle.Proxy.Forwarding;
using SeniorsInTheMiddle.Proxy.Forwarding.Tokenizer;

namespace Backend.Tests.Integration;

/// <summary>
/// That the forwarding path can actually be built out of the container, which is a different
/// question from whether each piece works.
///
/// The proxy is a graph of singletons resolved on the first proxied request, so a missing
/// registration is not a startup failure: the app comes up, the dashboard loads, and the first
/// device to send anything gets a 500 from somewhere three layers down. The other harnesses here
/// hand-build their own graph on purpose -- it is what lets them substitute a mutation -- so none
/// of them would notice.
/// </summary>
[TestClass]
public class ForwardProxyCompositionTests
{
    private WebApplicationFactory<Program> _factory = null!;

    [TestInitialize]
    public void Setup() => _factory = new WebApplicationFactory<Program>();

    [TestCleanup]
    public void Cleanup() => _factory?.Dispose();

    [TestMethod]
    public void The_Forwarding_Path_Resolves_From_The_Real_Registrations()
    {
        IServiceProvider services = _factory.Services;

        Assert.IsNotNull(services.GetRequiredService<IForwardProxy>());
        Assert.IsInstanceOfType<ReplacerService>(services.GetRequiredService<IBodyMutationFactory>());
        Assert.IsNotNull(services.GetRequiredService<AnonymizerVault>());
    }

    /// <summary>
    /// And that the vault is one vault. Resolved per request or per scope it would hold a map
    /// for exactly as long as the exchange that made it, which is the arrangement this replaced.
    /// </summary>
    [TestMethod]
    public void The_Vault_Is_Shared_By_Every_Exchange()
    {
        AnonymizerVault first = _factory.Services.GetRequiredService<AnonymizerVault>();

        using IServiceScope scope = _factory.Services.CreateScope();

        Assert.AreSame(first, scope.ServiceProvider.GetRequiredService<AnonymizerVault>());
    }

    /// <summary>The configured lifetime is the one the shipped settings name, not a default
    /// somewhere in the code that the file only appears to set.</summary>
    [TestMethod]
    public void The_Shipped_Settings_Are_The_Lifetime_In_Use()
    {
        VaultLifetime lifetime = _factory.Services.GetRequiredService<VaultLifetime>();

        Assert.AreEqual(TimeSpan.FromHours(48), lifetime.Ttl);
        Assert.AreEqual(512, lifetime.MaxClients);
    }
}
