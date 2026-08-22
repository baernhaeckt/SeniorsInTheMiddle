using SeniorsInTheMiddle.Proxy.Forwarding;

using Microsoft.Extensions.Configuration;

namespace Backend.Tests.Unit;

/// <summary>
/// Pins the three-listener layout: which ports are read, which combinations are refused,
/// and which of them count as proxy listeners. A silent fallback here would put the API on
/// a port devices are pointed at, or leave the proxy answering on the dashboard's port.
/// </summary>
[TestClass]
public class ProxyPortConfigurationTests
{
    private static ProxyPorts From(params (string Key, string Value)[] settings)
        => ProxyPorts.From(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build());

    [TestMethod]
    public void Defaults_Are_The_Documented_Three_Ports()
    {
        ProxyPorts ports = From();

        Assert.AreEqual(3128, ports.HttpProxy);
        Assert.AreEqual(3127, ports.HttpsProxy);
        Assert.AreEqual(8080, ports.Api);
    }

    [TestMethod]
    public void Every_Port_Is_Read_From_Configuration()
    {
        ProxyPorts ports = From(
            ("Proxy:HttpPort", "18128"),
            ("Proxy:HttpsPort", "18127"),
            ("Proxy:ApiPort", "18080"));

        Assert.AreEqual(18128, ports.HttpProxy);
        Assert.AreEqual(18127, ports.HttpsProxy);
        Assert.AreEqual(18080, ports.Api);
    }

    [TestMethod]
    public void Proxy_Listeners_Are_The_Two_Proxy_Ports_Only()
    {
        ProxyPorts ports = From();

        Assert.IsTrue(ports.IsProxyListener(3128));
        Assert.IsTrue(ports.IsProxyListener(3127));
        Assert.IsFalse(ports.IsProxyListener(8080));
    }

    /// <summary>
    /// TestServer reports a local port of 0. It must not look like a proxy listener, or
    /// every in-process test of the API would be answered with a 404 by the port guard.
    /// </summary>
    [TestMethod]
    public void Port_Zero_Is_Not_A_Proxy_Listener()
    {
        Assert.IsFalse(From().IsProxyListener(0));
    }

    [TestMethod]
    public void HttpsPort_Of_Zero_Turns_The_Tls_Proxy_Off()
    {
        ProxyPorts ports = From(("Proxy:HttpsPort", "0"));

        Assert.AreEqual(0, ports.HttpsProxy);
        Assert.IsFalse(ports.IsProxyListener(0));
        Assert.IsTrue(ports.IsProxyListener(3128));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    public void HttpPort_Cannot_Be_Turned_Off(string port)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => From(("Proxy:HttpPort", port)));
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("-1")]
    public void ApiPort_Cannot_Be_Turned_Off(string port)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => From(("Proxy:ApiPort", port)));
    }

    [TestMethod]
    public void Proxy_And_Api_On_The_Same_Port_Is_Refused()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => From(("Proxy:HttpPort", "8080")));
    }

    [TestMethod]
    public void Tls_Proxy_And_Api_On_The_Same_Port_Is_Refused()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => From(("Proxy:HttpsPort", "8080")));
    }

    [TestMethod]
    public void Both_Proxy_Listeners_On_The_Same_Port_Is_Refused()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => From(("Proxy:HttpsPort", "3128")));
    }
}
