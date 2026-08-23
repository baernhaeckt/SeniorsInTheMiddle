namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Handles one absolute-form HTTP request that reached the proxy port, forwarding it upstream
/// through the inspection pipeline.
/// </summary>
internal interface IForwardProxy
{
    Task HandleAsync(HttpContext context);
}
