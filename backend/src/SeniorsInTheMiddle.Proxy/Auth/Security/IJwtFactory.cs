using SeniorsInTheMiddle.Proxy.Auth.Domain;

namespace SeniorsInTheMiddle.Proxy.Auth.Security;

public interface IJwtFactory
{
    string GenerateToken(User user);
}
