using System.Net;

using SeniorsInTheMiddle.Proxy.Telemetry;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins how a connection becomes a row on the dashboard, and -- more importantly -- how it
/// becomes a key.
///
/// The label is written to be read from across a room, so it throws most of the address away.
/// The identity behind it must not, because a stand-in map is keyed on the same notion of "the
/// same device": two machines that collapse into one identity share one person's real names.
/// The label may be ambiguous; the identity may not.
/// </summary>
[TestClass]
public class ClientLabelerTests
{
    /// <summary>The User-Agent strings real devices send, shortened to the part that decides.</summary>
    [TestMethod]
    [DataRow("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15", "Tablet")]
    [DataRow("Mozilla/5.0 (Linux; Android 13; SM-X200) AppleWebKit/537.36 Tablet", "Tablet")]
    [DataRow("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)", "Phone")]
    [DataRow("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Mobile", "Phone")]
    [DataRow("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15", "Laptop")]
    [DataRow("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36", "Laptop")]
    [DataRow("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36", "Laptop")]
    [DataRow("curl/8.5.0", "Device")]
    [DataRow("", "Device")]
    [DataRow("   ", "Device")]
    [DataRow(null, "Device")]
    public void The_Device_Kind_Comes_From_The_User_Agent(string? userAgent, string expected)
    {
        string identity = ClientLabeler.Identity(IPAddress.Parse("10.0.0.4"), userAgent);

        Assert.AreEqual($"{expected}|10.0.0.4", identity);
    }

    /// <summary>
    /// An iPad's User-Agent says "Macintosh" too on recent iPadOS, so the tablet check has to
    /// win. Getting the order wrong labels every tablet in the room a laptop.
    /// </summary>
    [TestMethod]
    public void A_Tablet_That_Also_Claims_Macintosh_Is_Still_A_Tablet()
    {
        string identity = ClientLabeler.Identity(
            IPAddress.Parse("10.0.0.4"),
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) ... iPad");

        Assert.StartsWith("Tablet|", identity);
    }

    /// <summary>Case is not a signal; some clients lower-case the whole header.</summary>
    [TestMethod]
    public void The_Device_Kind_Ignores_Case()
    {
        Assert.AreEqual(
            ClientLabeler.Identity(IPAddress.Loopback, "mozilla/5.0 (iphone; cpu iphone os 17_0)"),
            ClientLabeler.Identity(IPAddress.Loopback, "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0)"));
    }

    /// <summary>
    /// Kestrel reports IPv4 clients as ::ffff:127.0.0.1 on a dual-stack socket. Left alone,
    /// that is both unreadable on the dashboard and a second identity for a device that
    /// already has one.
    /// </summary>
    [TestMethod]
    [DataRow("::ffff:127.0.0.1", "127.0.0.1")]
    [DataRow("::ffff:10.0.0.4", "10.0.0.4")]
    [DataRow("127.0.0.1", "127.0.0.1")]
    [DataRow("192.168.1.20", "192.168.1.20")]
    [DataRow("::1", "::1")]
    [DataRow("2001:db8::42", "2001:db8::42")]
    public void An_IPv4_Mapped_Address_Reads_As_IPv4(string address, string expected)
    {
        Assert.AreEqual(expected, ClientLabeler.Ip(IPAddress.Parse(address)));
    }

    /// <summary>A connection with no remote address is possible; it must not throw on the
    /// telemetry path, which runs on the request thread.</summary>
    [TestMethod]
    public void An_Unknown_Address_Is_Named_Rather_Than_Refused()
    {
        Assert.AreEqual("unknown", ClientLabeler.Ip(null));
        Assert.AreEqual("Device|unknown", ClientLabeler.Identity(null, null));
        Assert.AreEqual("Device · unknown", new ClientLabeler().Label(null, null));
    }

    [TestMethod]
    [DataRow("192.168.1.20", "Laptop · .20")]
    [DataRow("10.0.0.4", "Laptop · .4")]
    [DataRow("::ffff:192.168.1.20", "Laptop · .20")]
    [DataRow("2001:db8::42", "Laptop · 42")]
    public void The_Label_Keeps_Only_The_Last_Part_Of_The_Address(string address, string expected)
    {
        string label = new ClientLabeler().Label(
            IPAddress.Parse(address),
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        Assert.AreEqual(expected, label);
    }

    /// <summary>
    /// The point of keying on the identity rather than the label. Two tablets on the same
    /// subnet whose addresses differ only in the part the label shows are told apart; two whose
    /// addresses differ only in a part the label *hides* would share a label but must not share
    /// an identity, because a stand-in map hangs off it.
    /// </summary>
    [TestMethod]
    public void Two_Devices_The_Label_Cannot_Tell_Apart_Still_Have_Separate_Identities()
    {
        const string tablet = "Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)";

        string first = ClientLabeler.Identity(IPAddress.Parse("10.0.1.7"), tablet);
        string second = ClientLabeler.Identity(IPAddress.Parse("10.0.2.7"), tablet);

        ClientLabeler labeler = new();
        Assert.AreEqual(
            labeler.Label(IPAddress.Parse("10.0.1.7"), tablet),
            labeler.Label(IPAddress.Parse("10.0.2.7"), tablet),
            "The label is expected to be ambiguous; that is what the identity is for.");

        Assert.AreNotEqual(first, second);
    }

    /// <summary>Same device, same label, every request -- the dashboard groups rows by it.</summary>
    [TestMethod]
    public void The_Same_Connection_Gets_The_Same_Label_Every_Time()
    {
        ClientLabeler labeler = new();
        IPAddress address = IPAddress.Parse("10.0.0.4");
        const string userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";

        string first = labeler.Label(address, userAgent);

        for (int repeat = 0; repeat < 5; repeat++)
            Assert.AreEqual(first, labeler.Label(address, userAgent));
    }

    /// <summary>
    /// Labels are handed out from request threads, so the cache behind them is concurrent. A
    /// device that gets two different labels under load is two rows on the dashboard for one
    /// tablet.
    /// </summary>
    [TestMethod]
    public async Task Concurrent_Callers_Agree_On_One_Label()
    {
        ClientLabeler labeler = new();
        IPAddress address = IPAddress.Parse("10.0.0.4");
        const string userAgent = "Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X)";

        string[] labels = await Task.WhenAll(
            Enumerable.Range(0, 64).Select(_ => Task.Run(() => labeler.Label(address, userAgent))));

        Assert.HasCount(1, labels.Distinct().ToArray());
    }
}
