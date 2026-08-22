using System.Text.Json;

using SeniorsInTheMiddle.Proxy.Services.Pii;

namespace SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;

sealed class PrivacyCheckServiceClient(ServiceConnections services) : IPrivacyCheckServiceClient
{
    private readonly ServiceConnection connection = services.Get(ServiceConnections.PrivacyCheckService);

    public bool IsEnabled => connection.IsConfigured;

    public async Task<PrivacyRiskResult> RiskCheckAsync(
        string text,
        IReadOnlyList<string> replacedNames,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        ArgumentNullException.ThrowIfNull(replacedNames);

        if (replacedNames.Count == 0)
            return PrivacyRiskResult.Empty;

        JsonElement result = await connection.CallAsync(
            "risk_check",
            new { text, replaced_names = replacedNames },
            cancellationToken);

        if (result.ValueKind != JsonValueKind.Object)
            return PrivacyRiskResult.Empty;

        return result.Deserialize<PrivacyRiskResult>(PiiJson.Options) ?? PrivacyRiskResult.Empty;
    }
}
