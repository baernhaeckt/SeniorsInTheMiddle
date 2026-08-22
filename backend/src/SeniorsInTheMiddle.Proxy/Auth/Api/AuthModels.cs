namespace SeniorsInTheMiddle.Proxy.Auth.Api;

public record LoginRequest(string Username, string Password);

public record LoginResponse(string Token);

public record ProfileResponse(string Username, string Email);

public record RegisterRequest(string Username, string Email, string Password);

/// <summary>
/// The seeded demo credentials, for the login screen to prefill. Only ever returned when an
/// operator has opted in; see the <c>/demo-account</c> endpoint.
/// </summary>
public record DemoAccountResponse(string Username, string Password);
