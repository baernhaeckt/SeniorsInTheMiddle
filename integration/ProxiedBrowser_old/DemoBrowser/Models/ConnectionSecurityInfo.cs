using System.Security.Cryptography.X509Certificates;

namespace DemoBrowser.Models;

/// <summary>
/// Snapshot of the active page's connection security, populated from the DevTools
/// <c>Security.visibleSecurityStateChanged</c> event (the only way WebView2 exposes the TLS
/// details of a successful connection).
/// </summary>
public sealed class ConnectionSecurityInfo
{
    /// <summary>Host of the page this snapshot belongs to.</summary>
    public string Host { get; init; } = "";

    /// <summary>"secure", "insecure", "neutral", "insecure-broken" or "" when unknown.</summary>
    public string SecurityState { get; init; } = "";

    public string Protocol { get; init; } = "";

    public string KeyExchange { get; init; } = "";

    public string Cipher { get; init; } = "";

    /// <summary>Leaf first, then issuers as supplied by the server.</summary>
    public IReadOnlyList<X509Certificate2> Chain { get; init; } = [];

    public IReadOnlyList<string> Issues { get; init; } = [];

    /// <summary>
    /// True when the certificate was accepted because it chains to the in-memory proxy CA
    /// (i.e. the proxy is intercepting this connection).
    /// </summary>
    public bool TrustedViaProxyCa { get; init; }

    public bool IsHttps => !string.IsNullOrEmpty(Protocol) || Chain.Count > 0;

    public X509Certificate2? Leaf => Chain.Count > 0 ? Chain[0] : null;
}
