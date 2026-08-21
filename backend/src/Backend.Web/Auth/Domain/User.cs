namespace Backend.Web.Auth.Domain;

public class User
{
    public User(string username, string email)
    {
        Username = username;
        Email = email;
    }

    public string Username { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
}
