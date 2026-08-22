namespace SeniorsInTheMiddle.Proxy.Services.PrivacyCheck;

/// <summary>The re-identification risk check (services/privacy_check_service), over its unix socket.</summary>
public interface IPrivacyCheckServiceClient
{
    /// <summary>False when <c>Services:PrivacyCheck:SocketPath</c> is empty; calls then throw
    /// <see cref="ServiceUnavailableException"/>.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// How likely it is that <paramref name="replacedNames"/> can be recovered from
    /// <paramref name="text"/>, the text they were replaced in. Never null; an empty result
    /// when <paramref name="replacedNames"/> is empty. The python side runs an MCMC sampler
    /// per call, so this takes tens of seconds -- do not call it on the request path.
    /// </summary>
    Task<PrivacyRiskResult> RiskCheckAsync(string text, IReadOnlyList<string> replacedNames, CancellationToken cancellationToken = default);
}
