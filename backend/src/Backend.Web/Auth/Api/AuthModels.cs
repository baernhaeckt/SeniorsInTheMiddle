namespace Backend.Web.Auth.Api;

public record LoginRequest(string Username, string Password);

public record LoginResponse(string Token);

public record ProfileResponse(string Username, string Email);

public record RegisterRequest(string Username, string Email, string Password);
