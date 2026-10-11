using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace CodeSpace.E2ETests.Infrastructure;

/// <summary>
/// Mints a token the API will still believe.
///
/// <para>Nine E2E classes were each building their own JWT with a subject claim, which stopped being
/// enough the moment sessions became revocable: a token now also has to carry the account's security
/// stamp, and the server compares it on every request. One helper rather than nine copies, because
/// nine copies is how the tenth forgets.</para>
/// </summary>
public static class TestToken
{
    /// <summary>
    /// The stamp the harness seeds accounts with and mints tokens under. Fixed so a seed and a token
    /// agree without threading a value through every test; revocation tests that need a real rotation
    /// read the live value instead (see <see cref="ForAsync"/>).
    /// </summary>
    public static readonly Guid SeedStamp = new("11111111-2222-3333-4444-555555555555");

    public static async Task<string> ForAsync(TaskLaunchApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var stamp = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>()
            .User.AsNoTracking().Where(u => u.Id == userId).Select(u => u.SecurityStamp).SingleAsync();

        return Mint(userId, stamp);
    }

    public static string Mint(Guid userId, Guid stamp) => Mint(userId, stamp, DateTime.UtcNow.AddHours(1));

    /// <summary>A token with a chosen <c>exp</c>, valid from an hour before it, so a test can mint one already inside the handler's clock skew.</summary>
    public static string Mint(Guid userId, Guid stamp, DateTime expiresUtc)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(SessionValidator.SecurityStampClaim, stamp.ToString()),
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TaskLaunchApiFactory.JwtKey));
        var jwt = new JwtSecurityToken(claims: claims, notBefore: expiresUtc.AddHours(-1), expires: expiresUtc, signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
