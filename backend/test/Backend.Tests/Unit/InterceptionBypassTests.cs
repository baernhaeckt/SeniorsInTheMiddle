using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins which destinations go unread. Every entry on this list is a hole in the inspection --
/// nothing sent to a bypassed host is scanned and nothing coming back is restored -- so the
/// matching has to cover what was meant and nothing else. A suffix rule that straddled a label
/// boundary would silently exempt a host nobody listed.
/// </summary>
[TestClass]
public class InterceptionBypassTests
{
    private static InterceptionBypass From(params string[] hosts)
    {
        ConfigurationBuilder builder = new();

        builder.AddInMemoryCollection(hosts
            .Select((host, index) => new KeyValuePair<string, string?>($"Proxy:BypassHosts:{index}", host)));

        return new InterceptionBypass(builder.Build(), NullLogger<InterceptionBypass>.Instance);
    }

    /// <summary>
    /// Nothing is exempt unless a configuration says so. A name compiled in as a default would be
    /// one an operator cannot see in their own settings file and cannot take out.
    /// </summary>
    [TestMethod]
    public void No_Configuration_Bypasses_Nothing()
    {
        InterceptionBypass bypass = new(new ConfigurationBuilder().Build(), NullLogger<InterceptionBypass>.Instance);

        Assert.IsFalse(bypass.Covers("challenges.cloudflare.com"));
        Assert.IsFalse(bypass.Covers("chatgpt.com"));
    }

    /// <summary>
    /// The one that matters for the product: listing Turnstile must not take the site embedding it
    /// with it. ChatGPT's own traffic is the whole reason the proxy is in the path, so it stays
    /// intercepted and only the challenge's path is spared -- a decision the transformer makes
    /// after decryption, not this one.
    /// </summary>
    [TestMethod]
    public void Bypassing_Turnstile_Does_Not_Bypass_The_Site_Embedding_It()
    {
        InterceptionBypass bypass = From("challenges.cloudflare.com");

        Assert.IsTrue(bypass.Covers("challenges.cloudflare.com"));
        Assert.IsFalse(bypass.Covers("chatgpt.com"));
        Assert.IsFalse(bypass.Covers("api.openai.com"));
    }

    [TestMethod]
    public void An_Entry_Covers_Its_Subdomains()
    {
        InterceptionBypass bypass = From("example.com");

        Assert.IsTrue(bypass.Covers("example.com"));
        Assert.IsTrue(bypass.Covers("api.example.com"));
        Assert.IsTrue(bypass.Covers("a.b.example.com"));
    }

    /// <summary>The trap a bare EndsWith would fall into: an attacker registers the name.</summary>
    [TestMethod]
    public void A_Suffix_Match_Cannot_Straddle_A_Label()
    {
        InterceptionBypass bypass = From("example.com");

        Assert.IsFalse(bypass.Covers("notexample.com"));
        Assert.IsFalse(bypass.Covers("example.com.evil.net"));
    }

    [TestMethod]
    public void Matching_Ignores_Case_And_A_Trailing_Root_Dot()
    {
        InterceptionBypass bypass = From("Example.COM");

        Assert.IsTrue(bypass.Covers("EXAMPLE.com"));
        Assert.IsTrue(bypass.Covers("api.example.com."));
    }

    [TestMethod]
    public void A_Leading_Wildcard_Means_The_Domain_And_Everything_Under_It()
    {
        InterceptionBypass bypass = From("*.example.com");

        Assert.IsTrue(bypass.Covers("example.com"));
        Assert.IsTrue(bypass.Covers("api.example.com"));
    }

    [TestMethod]
    public void An_Empty_List_Bypasses_Nothing()
    {
        InterceptionBypass bypass = From();

        Assert.IsFalse(bypass.Covers("challenges.cloudflare.com"));
        Assert.IsFalse(bypass.Covers("example.com"));
    }
}
