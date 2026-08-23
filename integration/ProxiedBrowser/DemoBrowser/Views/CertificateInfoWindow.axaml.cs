using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DemoBrowser.Models;

namespace DemoBrowser.Views;

/// <summary>Lock-icon popup: connection summary, TLS parameters and the certificate chain of the current page.</summary>
public partial class CertificateInfoWindow : Window
{
    private readonly ConnectionSecurityInfo _info;

    /// <summary>One certificate as the chain list shows it: display name, its place in the
    /// chain (leaf, intermediate, root) and the certificate behind it.</summary>
    private sealed record ChainEntry(string Name, string Role, X509Certificate2 Certificate);

    public CertificateInfoWindow() : this(new ConnectionSecurityInfo(), "")
    {
    }

    public CertificateInfoWindow(ConnectionSecurityInfo info, string pageUrl)
    {
        InitializeComponent();
        _info = info;

        var host = string.IsNullOrEmpty(info.Host) && Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) ? uri.Host : info.Host;
        HeaderHost.Text = string.IsNullOrEmpty(host) ? "Connection security" : host;

        var isHttps = pageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var (glyph, title, detail, color) = (info.SecurityState, isHttps, info.TrustedViaProxyCa) switch
        {
            ("insecure-broken", _, _) => ("⚠", "Connection is not secure", "The certificate could not be validated. " + string.Join(", ", info.Issues), "#FF8A3D56"),
            ("secure", _, true) or (_, true, true) => ("🔒", "Secure · intercepted by the PII proxy",
                "This connection is encrypted to the proxy. The certificate was issued by the proxy CA that this browser trusts in-process only — nothing was installed in the system certificate store.", "#FF288879"),
            ("secure", _, false) or (_, true, false) => ("🔒", "Connection is secure", "Your connection to this site is encrypted.", "#FF288879"),
            (_, false, _) when string.IsNullOrEmpty(pageUrl) => ("◎", "No page loaded", "", "#FF949DA0"),
            _ => ("🔓", "Not secure", "This page is loaded over plain HTTP. Information you send can be read by others.", "#FF949DA0"),
        };

        HeaderGlyph.Text = glyph;
        SummaryGlyph.Text = glyph;
        SummaryTitle.Text = title;
        SummaryDetail.Text = detail;
        SummaryDetail.IsVisible = !string.IsNullOrEmpty(detail);
        SummaryBadge.Background = new SolidColorBrush(Color.Parse(color));

        ProtocolText.Text = Fallback(info.Protocol);
        KeyExchangeText.Text = Fallback(info.KeyExchange);
        CipherText.Text = Fallback(info.Cipher);

        var entries = info.Chain.Select((c, i) => new ChainEntry(
            FriendlyName(c),
            i == 0 ? "Server certificate" : IsSelfSigned(c) ? "Root CA" : "Intermediate CA",
            c)).ToList();
        ChainList.ItemsSource = entries;
        if (entries.Count > 0)
        {
            ChainList.SelectedIndex = 0;
        }
        else
        {
            ShowCertificate(null);
        }
    }

    private void OnChainSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ShowCertificate((ChainList.SelectedItem as ChainEntry)?.Certificate);

    private void ShowCertificate(X509Certificate2? cert)
    {
        var has = cert is not null;
        NoCertText.IsVisible = !has;
        CertGrid.IsVisible = has;
        CopyButton.IsEnabled = has;
        if (cert is null)
        {
            return;
        }

        SubjectText.Text = cert.Subject;
        IssuerText.Text = cert.Issuer;
        SanText.Text = Fallback(SubjectAlternativeNames(cert));
        ValidFromText.Text = cert.NotBefore.ToString("f");
        ValidToText.Text = cert.NotAfter.ToString("f") + (cert.NotAfter < DateTime.Now ? "  (expired)" : "");
        KeyText.Text = PublicKeyDescription(cert);
        SignatureText.Text = cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value ?? "";
        SerialText.Text = cert.SerialNumber;
        FingerprintText.Text = string.Join(":", Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(c => new string(c)));
    }

    private async void OnCopyPemClick(object? sender, RoutedEventArgs e)
    {
        if ((ChainList.SelectedItem as ChainEntry)?.Certificate is { } cert && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(cert.ExportCertificatePem());
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private static string Fallback(string value) => string.IsNullOrEmpty(value) ? "—" : value;

    private static string FriendlyName(X509Certificate2 cert)
    {
        var cn = cert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        return string.IsNullOrEmpty(cn) ? cert.Subject : cn;
    }

    private static bool IsSelfSigned(X509Certificate2 cert) => string.Equals(cert.Subject, cert.Issuer, StringComparison.Ordinal);

    private static string SubjectAlternativeNames(X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509SubjectAlternativeNameExtension san)
            {
                return string.Join(", ", san.EnumerateDnsNames());
            }
        }

        return "";
    }

    private static string PublicKeyDescription(X509Certificate2 cert)
    {
        try
        {
            using var rsa = cert.GetRSAPublicKey();
            if (rsa is not null)
            {
                return $"RSA {rsa.KeySize} bit";
            }

            using var ecdsa = cert.GetECDsaPublicKey();
            if (ecdsa is not null)
            {
                return $"ECDSA {ecdsa.KeySize} bit";
            }
        }
        catch (CryptographicException)
        {
        }

        return cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "—";
    }
}
