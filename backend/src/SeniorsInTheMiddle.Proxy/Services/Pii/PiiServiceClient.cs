using System.Text.Json;

namespace SeniorsInTheMiddle.Proxy.Services.Pii;

sealed class PiiServiceClient(ServiceConnections services) : IPiiServiceClient
{
    private readonly ServiceConnection connection = services.Get(ServiceConnections.PiiService);

    public bool IsEnabled => connection.IsConfigured;

    public async Task<PiiAnalyzeResult> AnalyzeAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);

        JsonElement result = await connection.CallAsync("analyze", new { text }, cancellationToken);

        // "nothing found" comes back as {} rather than a zero-count result
        if (result.ValueKind != JsonValueKind.Object || !result.EnumerateObject().Any())
            return PiiAnalyzeResult.Empty;

        return result.Deserialize<PiiAnalyzeResult>(PiiJson.Options) ?? PiiAnalyzeResult.Empty;
    }

    public async Task<string> ReplacementTextAsync(string piiType, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(piiType);

        JsonElement result = await connection.CallAsync("replacement_text", new { pii_type = piiType }, cancellationToken);
        return result.ValueKind == JsonValueKind.String ? result.GetString() ?? string.Empty : string.Empty;
    }
}
