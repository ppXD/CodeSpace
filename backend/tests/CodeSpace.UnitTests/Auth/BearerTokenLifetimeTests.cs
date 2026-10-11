using System.Security.Claims;
using CodeSpace.Core.Services.Auth;
using Shouldly;

namespace CodeSpace.UnitTests.Auth;

/// <summary>
/// When a request that outlives the door (the run-ledger tail) must stop serving a token: at the same instant the bearer
/// handler stops accepting it, which is <c>exp</c> plus the clock skew, boundary included. A principal with no readable
/// <c>exp</c> is never accepted, because every token the handler lets through carries one.
/// </summary>
[Trait("Category", "Unit")]
public class BearerTokenLifetimeTests
{
    private static readonly DateTimeOffset Expiry = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-3600, true)]   // well inside the token's own lifetime
    [InlineData(0, true)]       // at exp: the skew still covers it
    [InlineData(300, true)]     // exactly exp + skew: the handler's check is inclusive
    [InlineData(301, false)]    // one second past exp + skew: the handler answers 401 from here on
    public void Accepts_a_token_until_its_expiry_plus_the_clock_skew(int secondsPastExpiry, bool accepted)
    {
        var principal = PrincipalWithExpiry(Expiry.ToUnixTimeSeconds().ToString());

        BearerTokenLifetime.Accepts(principal, Expiry.AddSeconds(secondsPastExpiry)).ShouldBe(accepted);
    }

    [Theory]
    [InlineData(null)]          // no exp claim at all
    [InlineData("")]
    [InlineData("tomorrow")]    // not a NumericDate
    public void Never_accepts_a_principal_without_a_readable_expiry(string? exp)
    {
        BearerTokenLifetime.Accepts(PrincipalWithExpiry(exp), Expiry.AddHours(-1)).ShouldBeFalse();
    }

    [Fact]
    public void Never_accepts_an_absent_principal()
    {
        BearerTokenLifetime.Accepts(null, Expiry.AddHours(-1)).ShouldBeFalse();
    }

    /// <summary>The handler's skew. Moving it moves the moment an open tail ends; pinned so that is a visible decision.</summary>
    [Fact]
    public void ClockSkew_is_five_minutes()
    {
        BearerTokenLifetime.ClockSkew.ShouldBe(TimeSpan.FromMinutes(5));
    }

    private static ClaimsPrincipal PrincipalWithExpiry(string? exp)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()) };
        if (exp != null) claims.Add(new Claim("exp", exp, ClaimValueTypes.Integer64));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }
}
