namespace DemoBrowser.Models;

/// <summary>
/// Persisted application settings (%LOCALAPPDATA%\DemoBrowser\settings.json).
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// "http" or "https". A proxy on port 443 may be a plain HTTP CONNECT proxy or a TLS-terminating
    /// proxy; the scheme lets both be expressed in the Chromium --proxy-server switch.
    /// </summary>
    public string ProxyScheme { get; set; } = "http";

    public string ProxyHost { get; set; } = "seniorsinthemiddle-backend.greensea-158b1300.northeurope.azurecontainerapps.io";

    public int ProxyPort { get; set; } = 3128;

    /// <summary>Comma/semicolon separated bypass list passed verbatim to --proxy-bypass-list. Empty = none.</summary>
    public string ProxyBypassList { get; set; } = "";

    /// <summary>HTTPS URL (publicly-trusted TLS) from which the proxy CA certificate (PEM or DER) is downloaded.</summary>
    public string CaCertUrl { get; set; } = "https://seniorsinthemiddle-backend.greensea-158b1300.northeurope.azurecontainerapps.io/ca.crt";

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
