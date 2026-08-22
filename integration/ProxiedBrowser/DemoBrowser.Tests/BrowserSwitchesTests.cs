using DemoBrowser.Models;
using DemoBrowser.Services;
using Xunit;

namespace DemoBrowser.Tests;

/// <summary>
/// The Chromium switches are the only thing that decides whether traffic goes through the proxy: they are read
/// from the browser process command line once, at engine start. Getting them wrong is silent — the browser just
/// connects somewhere else.
/// </summary>
public class BrowserSwitchesTests
{
    private static AppSettings Settings(Action<AppSettings>? configure = null)
    {
        var settings = new AppSettings
        {
            ProxyScheme = "http",
            ProxyHost = "proxy.example.com",
            ProxyPort = 3128,
            ProxyBypassList = "",
        };
        configure?.Invoke(settings);
        return settings;
    }

    [Fact]
    public void HttpProxy_IsPassedAsProxyServerSwitch()
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(Settings());

        var proxy = Assert.Single(switches);
        Assert.Equal("proxy-server", proxy.Key);
        Assert.Equal("http://proxy.example.com:3128", proxy.Value);
    }

    [Fact]
    public void HttpsScheme_SwitchesTheProxyUrlScheme()
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(Settings(s =>
        {
            s.ProxyScheme = "https";
            s.ProxyPort = AppSettings.DefaultHttpsProxyPort;
        }));

        Assert.Equal($"https://proxy.example.com:{AppSettings.DefaultHttpsProxyPort}", switches.Single(s => s.Key == "proxy-server").Value);
    }

    [Theory]
    [InlineData("HTTPS", "https")]
    [InlineData("Https", "https")]
    [InlineData("http", "http")]
    [InlineData("something-else", "http")]
    public void ProxyScheme_IsMatchedCaseInsensitively_AndFallsBackToHttp(string configured, string expected)
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(Settings(s => s.ProxyScheme = configured));

        Assert.StartsWith(expected + "://", switches.Single(s => s.Key == "proxy-server").Value);
    }

    [Fact]
    public void ProxyHost_IsTrimmed()
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(Settings(s => s.ProxyHost = "  proxy.example.com  "));

        Assert.Equal("http://proxy.example.com:3128", switches.Single(s => s.Key == "proxy-server").Value);
    }

    [Fact]
    public void BypassList_IsPassedWhenSet()
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(Settings(s => s.ProxyBypassList = " localhost;*.corp.example.com "));

        Assert.Equal("localhost;*.corp.example.com", switches.Single(s => s.Key == "proxy-bypass-list").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BypassList_IsOmittedWhenBlank(string bypassList)
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(Settings(s => s.ProxyBypassList = bypassList));

        Assert.DoesNotContain(switches, s => s.Key == "proxy-bypass-list");
    }

    [Fact]
    public void UseProxyFalse_ConnectsDirectlyAndPassesNoProxySettings()
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(Settings(s =>
        {
            s.UseProxy = false;
            s.ProxyBypassList = "localhost";
        }));

        var only = Assert.Single(switches);
        Assert.Equal("no-proxy-server", only.Key);
        Assert.DoesNotContain(switches, s => s.Key == "proxy-server");
    }

    /// <summary>
    /// Without <c>--no-proxy-server</c> Chromium silently falls back to the system proxy configuration, so
    /// "proxy off" would not mean "direct" on a machine that has one configured.
    /// </summary>
    [Fact]
    public void UseProxyFalse_RendersAsValuelessSwitch()
    {
        var arguments = BrowserEnvironmentService.BuildBrowserArguments(Settings(s => s.UseProxy = false));

        Assert.Equal("--no-proxy-server", arguments);
    }

    [Fact]
    public void Arguments_RenderEverySwitchAsKeyValuePair()
    {
        var arguments = BrowserEnvironmentService.BuildBrowserArguments(Settings(s => s.ProxyBypassList = "localhost"));

        Assert.Equal("--proxy-server=http://proxy.example.com:3128 --proxy-bypass-list=localhost", arguments);
    }

    [Fact]
    public void Clone_CarriesUseProxy()
    {
        var clone = Settings(s => s.UseProxy = false).Clone();

        Assert.False(clone.UseProxy);
    }

    [Fact]
    public void UseProxy_DefaultsToOn()
    {
        Assert.True(new AppSettings().UseProxy);
    }

    /// <summary>
    /// With ProxyScheme = https, Chromium speaks TLS to the proxy itself. That certificate never reaches
    /// OnCertificateError — Chromium refuses the connection and every tab stays blank — so the pin established
    /// before the engine starts is the only thing that lets the browser reach the proxy at all.
    /// </summary>
    [Fact]
    public void ProxyTlsPin_IsPassedAsIgnoreCertificateErrorsSpkiList()
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(
            Settings(s => s.ProxyScheme = "https"),
            ["AAAApin1="]);

        var pin = Assert.Single(switches, s => s.Key == "ignore-certificate-errors-spki-list");
        Assert.Equal("AAAApin1=", pin.Value);
    }

    [Fact]
    public void ProxyTlsPins_AreJoinedWithCommas_BlanksAndDuplicatesDropped()
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(
            Settings(),
            [" pin-a= ", "", "pin-b=", "pin-a=", "   "]);

        var pin = Assert.Single(switches, s => s.Key == "ignore-certificate-errors-spki-list");
        Assert.Equal("pin-a=,pin-b=", pin.Value);
    }

    [Fact]
    public void NoProxyTlsPin_LeavesTheSwitchOff()
    {
        Assert.DoesNotContain(BrowserEnvironmentService.BuildBrowserSwitches(Settings()),
            s => s.Key == "ignore-certificate-errors-spki-list");
        Assert.DoesNotContain(BrowserEnvironmentService.BuildBrowserSwitches(Settings(), []),
            s => s.Key == "ignore-certificate-errors-spki-list");
    }

    /// <summary>A pin must never widen the trust decision when the proxy is switched off entirely.</summary>
    [Fact]
    public void ProxyOff_IgnoresPinsAndStaysOnNoProxyServer()
    {
        var switches = BrowserEnvironmentService.BuildBrowserSwitches(
            Settings(s => s.UseProxy = false),
            ["AAAApin1="]);

        var only = Assert.Single(switches);
        Assert.Equal("no-proxy-server", only.Key);
    }
}
