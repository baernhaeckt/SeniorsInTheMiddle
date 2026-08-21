using SeniorsInTheMiddle.Proxy.Auth.Domain;

namespace SeniorsInTheMiddle.Proxy.Auth.Storage;

public interface IUserStore
{
    Task<User?> FindByUsernameAsync(string username);

    Task SaveAsync(User user, string password);

    Task<User?> VerifyPassword(string username, string password);
}
