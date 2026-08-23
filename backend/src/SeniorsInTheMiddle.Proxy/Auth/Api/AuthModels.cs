namespace SeniorsInTheMiddle.Proxy.Auth.Api;

/// <summary>Credentials posted to <c>/login</c>. The password arrives base64-encoded.</summary>
public record LoginRequest(string Username, string Password);

/// <summary>The signed JWT a successful login hands back, for the client to bear on later calls.</summary>
public record LoginResponse(string Token);

/// <summary>The authenticated caller's own account, as returned by <c>/me</c>.</summary>
public record ProfileResponse(string Username, string Email);

/// <summary>A new account posted to <c>/register</c>. The password arrives base64-encoded.</summary>
public record RegisterRequest(string Username, string Email, string Password);

/// <summary>
/// The seeded demo credentials, for the login screen to prefill. Only ever returned when an
/// operator has opted in; see the <c>/demo-account</c> endpoint.
/// </summary>
public record DemoAccountResponse(string Username, string Password);
