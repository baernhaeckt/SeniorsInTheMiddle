namespace SeniorsInTheMiddle.Proxy.Services.Pii;

/// <summary>The PII detection service (services/pii_service), over its unix socket.</summary>
public interface IPiiServiceClient
{
    /// <summary>False when <c>Services:Pii:SocketPath</c> is empty; calls then throw
    /// <see cref="ServiceUnavailableException"/>.</summary>
    bool IsEnabled { get; }

    /// <summary>Finds PII entities in <paramref name="text"/>. Never null; an empty result
    /// when nothing was found.</summary>
    Task<PiiAnalyzeResult> AnalyzeAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>A plausible fake value for a PII type name, e.g. <c>PERSON</c>.</summary>
    Task<string> ReplacementTextAsync(string piiType, CancellationToken cancellationToken = default);
}
