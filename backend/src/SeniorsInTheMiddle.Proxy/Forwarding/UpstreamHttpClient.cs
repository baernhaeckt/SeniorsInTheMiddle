using System.Net;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// The client every forwarded request leaves through.
///
/// One instance for the process, so connections to a destination are pooled across requests
/// and across client connections. That matters most for intercepted HTTPS: a tunnel no longer
/// owns an upstream connection for its lifetime, it borrows one per request like everything
/// else.
///
/// Redirects, cookies, decompression and nested proxying are all off. A forward proxy passes
/// on what it was given and returns what it got; anything decided here would be a decision
/// the client can neither see nor undo.
/// </summary>
sealed class UpstreamHttpClient : HttpMessageInvoker
{
    public UpstreamHttpClient() : this(CreateHandler())
    {
    }

    /// <summary>Takes the handler so a test can reach a destination this process has no
    /// reason to trust.</summary>
    public UpstreamHttpClient(SocketsHttpHandler handler) : base(handler)
    {
    }

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
}
