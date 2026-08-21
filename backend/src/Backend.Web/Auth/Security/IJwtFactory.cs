using Backend.Web.Auth.Domain;

namespace Backend.Web.Auth.Security;

public interface IJwtFactory
{
    string GenerateToken(User user);
}
