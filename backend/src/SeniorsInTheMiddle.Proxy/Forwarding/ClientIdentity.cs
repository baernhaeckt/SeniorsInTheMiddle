using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Which device a request came from, as far as a forward proxy can tell: the address it
/// connects from and the kind of thing its User-Agent claims to be.
///
/// A type rather than a bare string because of what is keyed on it: a mutation's map from
/// stand-in to real value outlives the exchange that created it, and this key is the only thing
/// standing between one person's session and another's. Naming it keeps a caller from passing
/// a hostname by accident.
///
/// It is a guess, deliberately a conservative one. Two devices behind the same NAT running the
/// same browser look identical here and share a session they should not; nothing visible to a
/// proxy tells them apart. It does not make the opposite mistake: one device keeps one identity
/// for as long as its address lease lasts, which is what makes a value hidden in one request
/// restorable in the next.
/// </summary>
public readonly record struct ClientIdentity(string Value)
{
    public static ClientIdentity Of(HttpContext context)
        => new(ClientLabeler.Identity(
            context.Connection.RemoteIpAddress,
            context.Request.Headers.UserAgent));

    public override string ToString() => Value;
}
