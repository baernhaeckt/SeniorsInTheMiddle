using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

using SeniorsInTheMiddle.Proxy.Auth.Domain;

using Microsoft.IdentityModel.Tokens;

namespace SeniorsInTheMiddle.Proxy.Auth.Security;

/// <summary>
/// Issues HMAC-SHA256 signed JWTs from the <c>Jwt:*</c> configuration section.
/// </summary>
public class JwtFactory : IJwtFactory
{
    private readonly IConfiguration _configuration;
    private readonly SymmetricSecurityKey _key;

    public JwtFactory(IConfiguration configuration)
    {
        _configuration = configuration;
        string jwtKey = _configuration["Jwt:Key"] ?? throw new InvalidOperationException("JWT Key not configured");
        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
    }

    public string GenerateToken(User user)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, user.Username),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Email, user.Email),
        ];

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            // Long-lived on purpose: the proxy has no refresh flow, and a demo session that
            // expires mid-run is worse than a token that outlives the machine it was issued on.
            Expires = DateTime.UtcNow.AddHours(48),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256Signature),
            Issuer = _configuration["Jwt:Issuer"],
            Audience = _configuration["Jwt:Audience"]
        };

        SecurityToken token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
