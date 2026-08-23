using System.Text.Json;

using SeniorsInTheMiddle.Proxy.Services.Pii;

namespace SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;

/// <summary>
/// Talks to the Python privacy-check service over its unix socket, which scores how much a
/// body still gives away once the detected names have been replaced. Callers must check
/// <see cref="IsEnabled"/> first: with no socket path configured, every call throws
/// <see cref="ServiceUnavailableException"/>.
/// </summary>
sealed class PrivacyCheckServiceClient(ServiceConnections services) : IPrivacyCheckServiceClient
{
    private readonly ServiceConnection _connection = services.Get(ServiceConnections.PrivacyCheckService);

    public bool IsEnabled => _connection.IsConfigured;

    public async Task<PrivacyRiskResult> RiskCheckAsync(
        string text,
        IReadOnlyList<string> replacedNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(replacedNames);

        if (replacedNames.Count == 0)
            return PrivacyRiskResult.Empty;

        JsonElement result = await _connection.CallAsync(
            "risk_check",
            new { text, replaced_names = replacedNames },
            cancellationToken);

        if (result.ValueKind != JsonValueKind.Object)
            return PrivacyRiskResult.Empty;

        return result.Deserialize<PrivacyRiskResult>(PiiJson.Options) ?? PrivacyRiskResult.Empty;
    }
}
