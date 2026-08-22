namespace SeniorsInTheMiddle.Proxy.Forwarding;

internal interface IForwardProxy
{
    Task HandleAsync(HttpContext context);
}
