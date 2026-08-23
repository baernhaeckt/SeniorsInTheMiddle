using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using SeniorsInTheMiddle.Proxy.Forwarding;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins the list of names this app answers to, which decides two unrelated things.
///
/// It is the certificate's subject alternative names, so a name missing from it is a TLS
/// warning on the dashboard. It is also what stops a device configured to use the proxy from
/// having its request for our own API forwarded back to us -- a name missing there is the
/// app proxying to itself, which reads as a hang rather than as a misconfiguration.
/// </summary>
[TestClass]
public class SelfHostNamesTests
{
    private static SelfHostNames From(params string[] configured)
        => new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(configured
                    .Select((name, index) => new KeyValuePair<string, string?>($"Proxy:HostNames:{index}", name)))
                .Build(),
            NullLogger<SelfHostNames>.Instance);

    /// <summary>Loopback in every spelling, always, so `dotnet run` works with no configuration.</summary>
    [TestMethod]
    public void Loopback_Is_Always_Answered_For()
    {
        SelfHostNames names = From();

        Assert.IsTrue(names.Contains("localhost"));
        Assert.IsTrue(names.Contains("127.0.0.1"));
        Assert.IsTrue(names.Contains("::1"));
    }

    /// <summary>The first entry becomes the certificate's subject, so the order is not
    /// incidental: a certificate whose subject is an address rather than a name looks wrong
    /// wherever a subject is displayed.</summary>
    [TestMethod]
    public void Localhost_Comes_First()
    {
        Assert.AreEqual("localhost", From("proxy.example.ch").Names[0]);
    }

    [TestMethod]
    public void A_Configured_Name_Is_Answered_For()
    {
        SelfHostNames names = From("proxy.example.ch", "10.0.0.9");

        Assert.IsTrue(names.Contains("proxy.example.ch"));
        Assert.IsTrue(names.Contains("10.0.0.9"));
    }

    /// <summary>Host names are compared case-insensitively everywhere else, and an
    /// absolute-form request carries whatever case the client typed.</summary>
    [TestMethod]
    public void Names_Are_Matched_Ignoring_Case()
    {
        SelfHostNames names = From("Proxy.Example.CH");

        Assert.IsTrue(names.Contains("proxy.example.ch"));
        Assert.IsTrue(names.Contains("PROXY.EXAMPLE.CH"));
        Assert.IsTrue(names.Contains("LocalHost"));
    }

    /// <summary>A YAML or env-var list picks up surrounding space easily, and a certificate
    /// with " proxy.example.ch" as a SAN matches nothing.</summary>
    [TestMethod]
    public void Surrounding_Space_Is_Trimmed()
    {
        SelfHostNames names = From("  proxy.example.ch  ");

        Assert.IsTrue(names.Contains("proxy.example.ch"));
    }

    /// <summary>An empty entry is what a trailing comma or an unset variable leaves behind;
    /// a certificate refuses a blank SAN.</summary>
    [TestMethod]
    public void Blank_Entries_Are_Dropped()
    {
        SelfHostNames names = From("", "   ", "proxy.example.ch");

        Assert.IsTrue(names.Contains("proxy.example.ch"));
        Assert.IsFalse(names.Contains(string.Empty));
        Assert.DoesNotContain(string.Empty, names.Names.ToArray());
    }

    /// <summary>
    /// Configuring "localhost" explicitly is the obvious thing to do and must not produce a
    /// certificate with the same SAN twice, which some clients reject.
    /// </summary>
    [TestMethod]
    public void A_Name_Appears_Once_However_Often_It_Is_Configured()
    {
        SelfHostNames names = From("localhost", "LOCALHOST", "proxy.example.ch", "proxy.example.ch");

        Assert.HasCount(names.Names.Count, names.Names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.HasCount(1, names.Names.Where(n => n.Equals("localhost", StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    /// <summary>Anything not configured is somebody else's host, and forwarding to it is the
    /// whole point of the proxy.</summary>
    [TestMethod]
    [DataRow("example.com")]
    [DataRow("chatgpt.com")]
    [DataRow("localhost.evil.com")]
    [DataRow("")]
    public void An_Unrelated_Host_Is_Not_Us(string host)
    {
        Assert.IsFalse(From("proxy.example.ch").Contains(host));
    }
}
