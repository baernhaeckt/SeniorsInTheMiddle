namespace DemoBrowser.Models;

/// <summary>
/// Persisted application settings (%LOCALAPPDATA%\DemoBrowser\settings.json).
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// "http" (plain HTTP CONNECT proxy, default port <see cref="DefaultHttpProxyPort"/>) or
    /// "https" (TLS-terminating proxy, default port <see cref="DefaultHttpsProxyPort"/>).
    /// </summary>
    public string ProxyScheme { get; set; } = "http";

    public const int DefaultHttpProxyPort = 3128;

    public const int DefaultHttpsProxyPort = 3127;

    public string ProxyHost { get; set; } = "seniorsinthemiddle-backend.greensea-158b1300.northeurope.azurecontainerapps.io";

    public int ProxyPort { get; set; } = DefaultHttpProxyPort;

    /// <summary>Comma/semicolon separated bypass list passed verbatim to --proxy-bypass-list. Empty = none.</summary>
    public string ProxyBypassList { get; set; } = "";

    /// <summary>
    /// URL from which the proxy CA certificate (PEM or DER) is downloaded. The proxy serves it (together with
    /// proxy.pac) on its plain-HTTP port. If the URL is https, full TLS validation is applied.
    /// </summary>
    public string CaCertUrl { get; set; } = "http://seniorsinthemiddle-backend.greensea-158b1300.northeurope.azurecontainerapps.io:3128/ca.cer";

    public string StartPage { get; set; } = "https://example.com";

    public AppSettings Clone() => new()
    {
        ProxyScheme = ProxyScheme,
        ProxyHost = ProxyHost,
        ProxyPort = ProxyPort,
        ProxyBypassList = ProxyBypassList,
        CaCertUrl = CaCertUrl,
        StartPage = StartPage,
    };
}
