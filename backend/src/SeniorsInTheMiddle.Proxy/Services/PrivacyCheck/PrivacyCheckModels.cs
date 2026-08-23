using System.Text.Json.Serialization;

namespace SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;

/// <summary>
/// Result of the python <c>risk_check</c> method (services/privacy_check_service). The
/// python side emits snake_case, see <see cref="Pii.PiiJson.Options"/>. <see cref="Risks"/>
/// holds the replaced name(s) with the highest re-identification probability.
/// </summary>
public sealed record PrivacyRiskResult
{
    public static readonly PrivacyRiskResult Empty = new();

    public IReadOnlyList<PrivacyRisk> Risks { get; init; } = [];

    [JsonIgnore]
    public bool HasRisks => Risks.Count > 0;

    /// <summary>0 when nothing was checked.</summary>
    [JsonIgnore]
    public double MaxRiskProbability => Risks.Count == 0 ? 0 : Risks.Max(risk => risk.RiskProbability);
}

/// <summary>One replaced name and the probability that it can be recovered from the text.</summary>
public sealed record PrivacyRisk
{
    public string Name { get; init; } = string.Empty;

    /// <summary>0..1, posterior mean of the Bayesian model on the python side.</summary>
    public double RiskProbability { get; init; }
}
