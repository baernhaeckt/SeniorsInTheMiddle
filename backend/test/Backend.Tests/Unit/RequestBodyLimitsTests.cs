using Microsoft.Extensions.Configuration;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins the one number that decides whether a body is inspected or waved through, and the
/// values that are refused rather than accepted and discovered later. The limit is held per
/// concurrent request, so a value that looks like "no limit" is a value that trades every
/// rewritten body for the process.
/// </summary>
[TestClass]
public class RequestBodyLimitsTests
{
    private static RequestBodyLimits From(params (string Key, string Value)[] settings)
        => RequestBodyLimits.From(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [TestMethod]
    public void Default_Is_One_Megabyte()
    {
        RequestBodyLimits limits = From();

        Assert.AreEqual(1024 * 1024, limits.MaxMutableBodyBytes);
        Assert.AreEqual(RequestBodyLimits.DefaultMaxMutableBodyBytes, limits.MaxMutableBodyBytes);
    }

    /// <summary>Pins the configuration key itself; a rename that misses it reads as a setting
    /// that has no effect.</summary>
    [TestMethod]
    public void Limit_Is_Read_From_Configuration()
    {
        RequestBodyLimits limits = From(("Proxy:MaxMutableBodyBytes", "65536"));

        Assert.AreEqual(65536, limits.MaxMutableBodyBytes);
    }

    /// <summary>Turning rewriting off is a supported configuration, not an error.</summary>
    [TestMethod]
    public void Zero_Turns_Rewriting_Off()
    {
        RequestBodyLimits limits = From(("Proxy:MaxMutableBodyBytes", "0"));

        Assert.AreEqual(0, limits.MaxMutableBodyBytes);
    }

    [TestMethod]
    public void Ceiling_Itself_Is_Accepted()
    {
        RequestBodyLimits limits = From(
            ("Proxy:MaxMutableBodyBytes", RequestBodyLimits.MaxAllowedMutableBodyBytes.ToString()));

        Assert.AreEqual(RequestBodyLimits.MaxAllowedMutableBodyBytes, limits.MaxMutableBodyBytes);
    }

    /// <summary>
    /// int.MaxValue is the obvious way to spell "no limit" and is exactly the value that turns
    /// one upload into an OutOfMemoryException, so it is refused where someone can still read
    /// the message.
    /// </summary>
    [TestMethod]
    [DataRow("-1")]
    [DataRow("-2147483648")]
    [DataRow("2147483647")]
    [DataRow("67108865")]
    public void Values_That_Cannot_Work_Are_Refused(string configured)
    {
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => From(("Proxy:MaxMutableBodyBytes", configured)));

        Assert.Contains("Proxy:MaxMutableBodyBytes", error.Message);
    }
}
