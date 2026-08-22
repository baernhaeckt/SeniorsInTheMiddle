namespace DemoBrowser.Services;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One recorded step of the proxy/certificate path. <paramref name="Detail"/> may span several lines.</summary>
public sealed record DiagnosticEntry(
    DateTime TimestampUtc,
    DiagnosticSeverity Severity,
    string Category,
    string Message,
    string? Detail = null)
{
    public string TimeText => TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff");

    public override string ToString() =>
        $"[{TimeText}] {Severity.ToString().ToUpperInvariant(),-7} {Category,-11} {Message}"
        + (string.IsNullOrEmpty(Detail) ? "" : "\n" + string.Join('\n', Detail.Split('\n').Select(l => "                                   " + l)));
}

/// <summary>
/// In-memory log of everything that decides whether a page loads behind the MITM proxy: the Chromium switches,
/// the CA download, and every certificate CEF could not validate itself.
///
/// WHY this exists: when a site stays blank behind the proxy, the interesting failure is invisible. Chromium logs
/// only its own verdict ("No matching issuer found") to cef.log — which is *expected* behind a MITM proxy and says
/// nothing about our own override in <see cref="CertificateService.HandleServerCertificateError"/>. This records
/// whether that override ran at all and, when it rejected a certificate, the <c>X509ChainStatus</c> flags
/// that explain why. <see cref="Views.DiagnosticsWindow"/> shows the result.
///
/// Written from CEF threads, read from the UI thread, so every access is locked. Bounded so a long session
/// cannot grow it without limit.
/// </summary>
public sealed class ProxyDiagnostics
{
    private const int MaxEntries = 500;

    private readonly Lock _gate = new();
    private readonly List<DiagnosticEntry> _entries = [];

    /// <summary>Raised (on the writing thread) whenever an entry was added or the log was cleared.</summary>
    public event EventHandler? Changed;

    public void Info(string category, string message, string? detail = null) =>
        Add(DiagnosticSeverity.Info, category, message, detail);

    public void Warning(string category, string message, string? detail = null) =>
        Add(DiagnosticSeverity.Warning, category, message, detail);

    public void Error(string category, string message, string? detail = null) =>
        Add(DiagnosticSeverity.Error, category, message, detail);

    public void Add(DiagnosticSeverity severity, string category, string message, string? detail = null)
    {
        lock (_gate)
        {
            _entries.Add(new DiagnosticEntry(DateTime.UtcNow, severity, category, message, detail));
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A copy of the current entries, oldest first.</summary>
    public IReadOnlyList<DiagnosticEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The whole log as text, for the "Copy" button in the diagnostics window.</summary>
    public string ToPlainText() => string.Join('\n', Snapshot().Select(e => e.ToString()));
}
