using SeniorsInTheMiddle.Proxy.Auth.Domain;

namespace SeniorsInTheMiddle.Proxy.Auth.Security;

/// <summary>Issues the bearer tokens that authenticate the frontend against the proxy's API.</summary>
public interface IJwtFactory
{
    string GenerateToken(User user);
}
