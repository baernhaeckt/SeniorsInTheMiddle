using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins what the blocklist refuses. A match here is invisible -- not forwarded, not traced,
/// not in the dashboard -- so it has to cover the paths named and nothing beside them.
/// </summary>
[TestClass]
public class BlocklistTests
{
    private static Blocklist From(params (string Host, string[] Paths)[] rules)
    {
        ConfigurationBuilder builder = new();

        builder.AddInMemoryCollection(rules.SelectMany(rule => rule.Paths.Select((path, index) =>
            new KeyValuePair<string, string?>($"Proxy:Blocked:{rule.Host}:{index}", path))));

        return new Blocklist(builder.Build(), NullLogger<Blocklist>.Instance);
    }

    [TestMethod]
    public void No_Configuration_Blocks_Nothing()
    {
        Blocklist blocklist = new(new ConfigurationBuilder().Build(), NullLogger<Blocklist>.Instance);

        Assert.IsTrue(blocklist.IsEmpty);
        Assert.IsFalse(blocklist.Covers(new Uri("https://www.youtube.com/watch?v=9uU95bql1UQ")));
    }

    [TestMethod]
    public void A_Listed_Path_On_The_Host_Or_A_Subdomain_Is_Blocked()
    {
        Blocklist blocklist = From(("youtube.com", ["/watch", "/shorts"]));

        Assert.IsTrue(blocklist.Covers(new Uri("https://www.youtube.com/watch?v=9uU95bql1UQ")));
        Assert.IsTrue(blocklist.Covers(new Uri("https://youtube.com/shorts/abc")));
        Assert.IsTrue(blocklist.Covers(new Uri("https://m.youtube.com/WATCH?v=x")));
    }

    /// <summary>The rest of the site keeps working; only the listed paths are refused.</summary>
    [TestMethod]
    public void Other_Paths_On_The_Host_Are_Forwarded()
    {
        Blocklist blocklist = From(("youtube.com", ["/watch"]));

        Assert.IsFalse(blocklist.Covers(new Uri("https://www.youtube.com/")));
        Assert.IsFalse(blocklist.Covers(new Uri("https://www.youtube.com/feed/subscriptions")));
        Assert.IsFalse(blocklist.Covers(new Uri("https://i.ytimg.com/vi/x/hqdefault.jpg")));
    }

    [TestMethod]
    public void A_Root_Path_Blocks_The_Whole_Host()
    {
        Blocklist blocklist = From(("tracker.example", ["/"]));

        Assert.IsTrue(blocklist.Covers(new Uri("https://tracker.example/")));
        Assert.IsTrue(blocklist.Covers(new Uri("https://cdn.tracker.example/pixel.gif?id=1")));
        Assert.IsFalse(blocklist.Covers(new Uri("https://example.com/")));
    }

    [TestMethod]
    public void A_Suffix_Match_Cannot_Straddle_A_Label()
    {
        Blocklist blocklist = From(("youtube.com", ["/watch"]));

        Assert.IsFalse(blocklist.Covers(new Uri("https://notyoutube.com/watch?v=x")));
        Assert.IsFalse(blocklist.Covers(new Uri("https://youtube.com.evil.net/watch?v=x")));
    }

    [TestMethod]
    public void A_Host_With_No_Paths_Is_Ignored()
    {
        Assert.IsTrue(From(("youtube.com", [])).IsEmpty);
    }
}
