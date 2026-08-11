using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Auth;

/// <summary>
/// E2E coverage for the password-rotation gate on the WIRE, through the REAL ASP.NET pipeline — JWT auth, the
/// mediator's PasswordRotationRequiredBehavior, and the GlobalExceptionFilter that turns its exception into a
/// response the SPA can branch on.
///
/// <para>The bug this pins: the filter had NO arm for PasswordRotationRequiredException, so a
/// <c>password_must_change</c> user hitting any guarded endpoint got the masked 500 from the default arm. The SPA
/// (frontend/src/api/request.ts) redirects to /change-password only on <c>403 + code=password_rotation_required</c>,
/// so the flagged user saw an opaque "unexpected error" on every page and could never reach the rotation form.
/// Asserting the exception type at the mediator seam (ChangePasswordFlowTests) cannot catch this — only the wire can.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + the real middleware/mediator/filter pipeline.
/// <c>GET /api/users/me</c> is the guarded endpoint under test: it goes through the mediator (MeQuery does NOT carry
/// IBypassPasswordRotationGuard) and needs no X-Team-Id, so a 403 here is the rotation gate and nothing else.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class PasswordRotationGateEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public PasswordRotationGateEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Flagged_user_calling_a_guarded_endpoint_gets_403_with_password_rotation_required()
    {
        var userId = await SeedUserAsync(passwordMustChange: true);

        var response = await SendAsync("/api/users/me", userId);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            customMessage: await DescribeAsync(response, "403 — the SPA only redirects to /change-password on a 403; a 500 (the old masked default arm) strands the flagged user"));

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        body.GetProperty("code").GetString().ShouldBe("password_rotation_required",
            customMessage: "the literal code frontend/src/api/request.ts branches on — a different string silently disables the redirect");
    }

    [Fact]
    public async Task Unflagged_user_calling_the_same_endpoint_is_unaffected()
    {
        // Control: proves the 403 above comes from the rotation flag and not from the endpoint being
        // unreachable for some other reason (missing team header, auth, seed shape).
        var userId = await SeedUserAsync(passwordMustChange: false);

        var response = await SendAsync("/api/users/me", userId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            customMessage: await DescribeAsync(response, "200 — an unflagged user must pass the rotation gate untouched"));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(string url, Guid userId)
    {
        // No X-Team-Id: /api/users/me reads the JWT only, so nothing but the rotation gate can 403 here.
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));

        return await _factory.CreateClient().SendAsync(request);
    }

    /// <summary>ApiUser reads PasswordMustChange from the DB (not a JWT claim), so the flag is seeded on the row.</summary>
    private async Task<Guid> SeedUserAsync(bool passwordMustChange)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();

        db.User.Add(new User
        {
            Id = userId,
            Email = $"rotation-{suffix}@test.local",
            Name = "Rotation E2E",
            PasswordMustChange = passwordMustChange,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId
        });

        await db.SaveChangesAsync();
        return userId;
    }

    private static string MintToken(Guid userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TaskLaunchApiFactory.JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(claims: claims, notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response, string expectation) =>
        $"GET /api/users/me expected {expectation}; got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}
