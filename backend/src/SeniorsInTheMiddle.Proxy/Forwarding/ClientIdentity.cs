using SeniorsInTheMiddle.Proxy.Telemetry;

namespace SeniorsInTheMiddle.Proxy.Forwarding;

/// <summary>
/// Which device a request came from, as far as a forward proxy can tell: the address it
/// connects from and the kind of thing its User-Agent claims to be.
///
/// It is a type rather than a bare string because of what is keyed on it. A mutation's map from
/// stand-in to real value outlives the single exchange that created it, and the key is the only
/// thing standing between one person's session and another's -- so the value that plays that
/// part is named, produced in one place, and not something a caller can pass a hostname to by
/// accident.
///
/// It is a guess, and deliberately a conservative one. Two devices behind the same NAT that run
/// the same kind of browser look identical here, and share a session they should not; nothing
/// visible to a proxy tells them apart. What it does not do is the opposite mistake: the same
/// device keeps the same identity for as long as its address lease does, which is what makes a
/// value hidden in one request restorable in the next.
/// </summary>
public readonly record struct ClientIdentity(string Value)
{
    public static ClientIdentity Of(HttpContext context)
        => new(ClientLabeler.Identity(
            context.Connection.RemoteIpAddress,
            context.Request.Headers.UserAgent));

    public override string ToString() => Value;
}
