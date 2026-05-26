using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Settings.Authentication;
using Microsoft.IdentityModel.Tokens;

namespace CodeSpace.Core.Services.Auth;

/// <summary>
/// Mints HS256 JWTs for sign-in success. Claims:
///   • <c>sub</c> / <c>nameid</c>  — user id (ApiUser reads NameIdentifier)
///   • <c>name</c>                  — display name
///   • <c>email</c>                 — email
/// Lifetime is 24 hours; refresh tokens aren't issued yet (rotation is a separate slice).
/// </summary>
public sealed class JwtTokenIssuer : IJwtTokenIssuer, ISingletonDependency
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private readonly JwtSymmetricKeySetting _keySetting;
    private readonly TimeProvider _clock;

    public JwtTokenIssuer(JwtSymmetricKeySetting keySetting, TimeProvider clock)
    {
        _keySetting = keySetting;
        _clock = clock;
    }

    public IssuedToken Issue(User user)
    {
        if (string.IsNullOrWhiteSpace(_keySetting.Value)) throw new InvalidOperationException("Authentication:Jwt:SymmetricKey is not configured; cannot mint tokens.");

        var issuedAt = _clock.GetUtcNow();
        var expiresAt = issuedAt + TokenLifetime;

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Email, user.Email)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_keySetting.Value));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            claims: claims,
            notBefore: issuedAt.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        var token = new JwtSecurityTokenHandler().WriteToken(jwt);

        return new IssuedToken { Token = token, ExpiresAt = expiresAt };
    }
}
