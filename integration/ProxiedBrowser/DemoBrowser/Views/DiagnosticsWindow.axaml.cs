using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DemoBrowser.Services;

namespace DemoBrowser.Views;

/// <summary>
/// Shows the proxy/certificate log collected by <see cref="ProxyDiagnostics"/> (Ctrl+Shift+D, or the ⚙ dialog).
///
/// WHY: behind the MITM proxy every HTTPS site fails Chromium's own validation and is rescued by
/// <see cref="CertificateService.HandleServerCertificateError"/>. When a page stays blank there is no way to tell
/// from the outside whether that override never ran, ran and rejected the certificate, or the CA never loaded.
/// cef.log only ever shows Chromium's verdict, which is "untrusted issuer" in all three cases.
/// </summary>
public partial class DiagnosticsWindow : Window
{
    private readonly ProxyDiagnostics _diagnostics;
    private readonly CertificateService _certificateService;
    private readonly BrowserEnvironment _environment;

    public DiagnosticsWindow(ProxyDiagnostics diagnostics, CertificateService certificateService, BrowserEnvironment environment)
    {
        InitializeComponent();
        _diagnostics = diagnostics;
        _certificateService = certificateService;
        _environment = environment;

        _diagnostics.Changed += OnDiagnosticsChanged;
        Closed += (_, _) => _diagnostics.Changed -= OnDiagnosticsChanged;

        Refresh();
    }

    /// <summary>Diagnostics are written from CEF threads, so refreshes are marshalled onto the UI thread.</summary>
    private void OnDiagnosticsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var settings = _environment.Settings;
        var ca = _certificateService.ProxyCa;

        SummaryText.Text = string.Join('\n',
        [
            "proxy    : " + (settings.UseProxy
                ? $"{settings.ProxyScheme}://{settings.ProxyHost}:{settings.ProxyPort}"
                : "disabled (UseProxy = false) — connecting directly"),
            "bypass   : " + (string.IsNullOrWhiteSpace(settings.ProxyBypassList) ? "(none)" : settings.ProxyBypassList),
            "switches : " + _environment.BrowserArguments,
            "CA url   : " + (settings.UseProxy ? settings.CaCertUrl : "(not used)"),
            "CA loaded: " + (ca is null ? "NO — every HTTPS site behind the proxy will fail" : $"yes — {ca.Subject} (thumbprint {ca.Thumbprint})"),
            "Chromium : " + _environment.ChromeVersion,
        ]);

        // The single most common cause of a blank page: the proxy is on but no CA was ever loaded.
        HintText.IsVisible = settings.UseProxy && ca is null;
        HintText.Text = "No proxy CA is loaded, so nothing the proxy re-signs can be trusted and every HTTPS page "
                        + "stays blank. Check the CA certificate URL in the settings dialog, and that the proxy serves "
                        + "it on its plain-HTTP port.";

        LogItems.ItemsSource = _diagnostics.Snapshot().Select(DiagnosticRow.From).ToList();
        LogScroller.ScrollToEnd();
    }

    private void OnClearClick(object? sender, RoutedEventArgs e) => _diagnostics.Clear();

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        var text = SummaryText.Text + "\n\n" + _diagnostics.ToPlainText();
        if (Clipboard is not null)
        {
            await Clipboard.SetTextAsync(text);
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

    /// <summary>
    /// One row of the log, with everything the template binds to already computed — that keeps the XAML free of
    /// value converters for what is only ever a colour and a visibility flag.
    /// </summary>
    private sealed record DiagnosticRow(string TimeText, string Category, string Message, string? Detail, IBrush Brush)
    {
        public bool HasDetail => !string.IsNullOrEmpty(Detail);

        public static DiagnosticRow From(DiagnosticEntry entry) => new(
            entry.TimeText,
            entry.Category,
            entry.Message,
            entry.Detail,
            entry.Severity switch
            {
                DiagnosticSeverity.Error => Brushes.Salmon,
                DiagnosticSeverity.Warning => Brushes.Gold,
                _ => Brushes.Gainsboro,
            });
    }
}
