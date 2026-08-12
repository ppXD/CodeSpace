using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Exceptions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Auth;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SignInFlowTests
{
    private readonly PostgresFixture _fixture;

    public SignInFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Seed_admin_can_sign_in_with_known_password()
    {
        // 0006 migration seeds admin@codespace.local / changeme123. This test verifies the
        // seed survives DbUp and the password hash matches the live Pbkdf2PasswordHasher.
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = "admin@codespace.local", Password = "changeme123" }).ConfigureAwait(false);

        result.Token.ShouldNotBeNullOrEmpty();
        result.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        result.User.Email.ShouldBe("admin@codespace.local");
        result.User.Name.ShouldBe("admin");
        result.User.Teams.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Seed_admin_can_sign_in_by_display_name_too()
    {
        // The handler accepts either email or display name. The seed admin's name is
        // "admin" — typing "admin" should resolve to the same user as the full email.
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = "admin", Password = "changeme123" }).ConfigureAwait(false);

        result.User.Email.ShouldBe("admin@codespace.local");
        result.User.Name.ShouldBe("admin");
    }

    /// <summary>
    /// Sign-in answers "what may I do" for the account it just authenticated, and the client caches
    /// that answer. The request itself is anonymous — nobody is signed in while it runs — so reading
    /// the grants off the ambient principal returned nothing and every instance capability stayed
    /// invisible until some later request happened to refresh it. Read them for the account instead.
    /// </summary>
    [Fact]
    public async Task Sign_in_carries_the_signed_in_account_own_instance_permissions()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = "admin@codespace.local", Password = "changeme123" }).ConfigureAwait(false);

        result.User.Permissions.ShouldContain(Permissions.TeamsCreate, "the seed admin holds the Admin role, which grants it");
    }

    [Fact]
    public async Task Sign_in_reports_no_instance_permissions_for_an_account_holding_none()
    {
        var (email, password) = await SeedUnprivilegedUserAsync().ConfigureAwait(false);

        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = email, Password = password }).ConfigureAwait(false);

        result.User.Permissions.ShouldBeEmpty("grants are per-account — an ungranted user must not inherit anyone else's");
    }

    [Fact]
    public async Task Wrong_password_throws_InvalidCredentials()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var act = async () => await mediator.Send(new SignInCommand { Name = "admin@codespace.local", Password = "wrong-password" }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Unknown_email_throws_InvalidCredentials()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var act = async () => await mediator.Send(new SignInCommand { Name = $"ghost-{Guid.NewGuid():N}@nowhere", Password = "anything" }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task User_with_no_password_hash_cannot_sign_in()
    {
        // The seeded SYSTEM user (id 00...01, email system@codespace.local) has no password_hash.
        // Even with the correct row present, sign-in must reject.
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var act = async () => await mediator.Send(new SignInCommand { Name = "system@codespace.local", Password = "anything" }).ConfigureAwait(false);

        await act.ShouldThrowAsync<InvalidCredentialsException>().ConfigureAwait(false);
    }

    [Fact]
    public async Task Email_lookup_is_case_insensitive()
    {
        using var scope = _fixture.BeginScope();
        var mediator = scope.Resolve<IMediator>();

        var result = await mediator.Send(new SignInCommand { Name = "ADMIN@CODESPACE.LOCAL", Password = "changeme123" }).ConfigureAwait(false);

        result.User.Email.ShouldBe("admin@codespace.local");
    }

    [Fact]
    public async Task SignIn_stamps_last_login_date()
    {
        // Snapshot the seed admin's LastLoginDate, sign in, then re-read. The column should
        // have advanced — proves the handler actually persisted the timestamp.
        Guid adminId;
        DateTimeOffset? before;

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var admin = await db.User.AsNoTracking().FirstAsync(u => u.Email == "admin@codespace.local").ConfigureAwait(false);
            adminId = admin.Id;
            before = admin.LastLoginDate;
        }

        using (var signInScope = _fixture.BeginScope())
        {
            await signInScope.Resolve<IMediator>().Send(new SignInCommand { Name = "admin@codespace.local", Password = "changeme123" }).ConfigureAwait(false);
        }

        using (var verifyScope = _fixture.BeginScope())
        {
            var db = verifyScope.Resolve<CodeSpaceDbContext>();
            var admin = await db.User.AsNoTracking().FirstAsync(u => u.Id == adminId).ConfigureAwait(false);
            admin.LastLoginDate.ShouldNotBeNull();
            if (before != null) admin.LastLoginDate.Value.ShouldBeGreaterThan(before.Value);
        }
    }

    [Fact]
    public void Issued_token_validates_against_configured_JWT_key()
    {
        // Round-trip the JWT through Microsoft.IdentityModel — same validation parameters as
        // AuthenticationExtension wires up for incoming requests.
        using var scope = _fixture.BeginScope();
        var issuer = scope.Resolve<IJwtTokenIssuer>();

        var user = new User { Id = Guid.NewGuid(), Email = "x@y", Name = "x" };
        var issued = issuer.Issue(user);

        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var key = scope.Resolve<CodeSpace.Core.Settings.Authentication.JwtSymmetricKeySetting>().Value;

        handler.ValidateToken(issued.Token, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(key))
        }, out var validatedToken);

        validatedToken.ShouldNotBeNull();
    }

    private async Task<(string Email, string Password)> SeedUnprivilegedUserAsync()
    {
        const string password = "plain-user-pw-123";

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var email = $"plain-{Guid.NewGuid():N}@x";
        db.User.Add(new User { Id = Guid.NewGuid(), Email = email, Name = email, PasswordHash = scope.Resolve<IPasswordHasher>().Hash(password) });

        await db.SaveChangesAsync().ConfigureAwait(false);

        return (email, password);
    }
}
