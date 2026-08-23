using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins what a detour catches. A rule here is a hole in the forwarding -- nothing matched is
/// inspected or delivered -- so it must cover the paths named and nothing beside them, and
/// it must never catch the URL it redirects to, or the client is bounced forever.
/// </summary>
[TestClass]
public class DetoursTests
{
    private const string Rickroll = "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    private static Detours From(params (string Key, string? Value)[] settings)
    {
        ConfigurationBuilder builder = new();

        builder.AddInMemoryCollection(settings.Select(setting =>
            new KeyValuePair<string, string?>($"Proxy:Detours:{setting.Key}", setting.Value)));

        return new Detours(builder.Build(), NullLogger<Detours>.Instance);
    }

    private static Detours YouTube() => From(
        ("youtube.com:Paths:0", "/watch"),
        ("youtube.com:Paths:1", "/shorts"),
        ("youtube.com:To", Rickroll));

    [TestMethod]
    public void No_Configuration_Detours_Nothing()
    {
        Detours detours = new(new ConfigurationBuilder().Build(), NullLogger<Detours>.Instance);

        Assert.IsTrue(detours.IsEmpty);
        Assert.IsNull(detours.For(new Uri("https://www.youtube.com/watch?v=9uU95bql1UQ")));
    }

    [TestMethod]
    public void A_Listed_Path_On_The_Host_Or_A_Subdomain_Is_Detoured()
    {
        Detours detours = YouTube();

        Assert.AreEqual(new Uri(Rickroll), detours.For(new Uri("https://www.youtube.com/watch?v=9uU95bql1UQ")));
        Assert.AreEqual(new Uri(Rickroll), detours.For(new Uri("https://youtube.com/shorts/abc")));
        Assert.AreEqual(new Uri(Rickroll), detours.For(new Uri("https://m.youtube.com/WATCH?v=x")));
    }

    /// <summary>The rest of the site keeps working; only the view-farm paths are caught.</summary>
    [TestMethod]
    public void Other_Paths_On_The_Host_Are_Forwarded()
    {
        Detours detours = YouTube();

        Assert.IsNull(detours.For(new Uri("https://www.youtube.com/")));
        Assert.IsNull(detours.For(new Uri("https://www.youtube.com/feed/subscriptions")));
        Assert.IsNull(detours.For(new Uri("https://i.ytimg.com/vi/x/hqdefault.jpg")));
    }

    [TestMethod]
    public void A_Suffix_Match_Cannot_Straddle_A_Label()
    {
        Detours detours = YouTube();

        Assert.IsNull(detours.For(new Uri("https://notyoutube.com/watch?v=x")));
        Assert.IsNull(detours.For(new Uri("https://youtube.com.evil.net/watch?v=x")));
    }

    /// <summary>
    /// The target is on the very path the rule catches. Without this the client would be
    /// redirected to the redirect, and the joke would never load.
    /// </summary>
    [TestMethod]
    public void The_Target_Itself_Is_Never_Detoured()
    {
        Detours detours = YouTube();

        Assert.IsNull(detours.For(new Uri(Rickroll)));
        Assert.IsNull(detours.For(new Uri("https://WWW.YouTube.com/watch?v=dQw4w9WgXcQ")));
    }

    /// <summary>A different query on the same path is still caught; only the exact target passes.</summary>
    [TestMethod]
    public void A_Different_Query_On_The_Target_Path_Is_Still_Detoured()
    {
        Detours detours = YouTube();

        Assert.IsNotNull(detours.For(new Uri("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42")));
    }

    [TestMethod]
    public void A_Rule_Missing_Its_Paths_Or_Target_Is_Ignored()
    {
        Assert.IsTrue(From(("youtube.com:To", Rickroll)).IsEmpty);
        Assert.IsTrue(From(("youtube.com:Paths:0", "/watch")).IsEmpty);
        Assert.IsTrue(From(("youtube.com:Paths:0", "/watch"), ("youtube.com:To", "not a url")).IsEmpty);
    }
}
