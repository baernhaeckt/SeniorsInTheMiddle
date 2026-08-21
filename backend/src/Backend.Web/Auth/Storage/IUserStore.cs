using Backend.Web.Auth.Domain;

namespace Backend.Web.Auth.Storage;

public interface IUserStore
{
    Task<User?> FindByUsernameAsync(string username);

    Task SaveAsync(User user, string password);

    Task<User?> VerifyPassword(string username, string password);
}
