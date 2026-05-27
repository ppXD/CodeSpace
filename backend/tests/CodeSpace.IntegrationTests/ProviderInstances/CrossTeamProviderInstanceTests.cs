using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.ProviderInstances;

/// <summary>
/// Confirms the multi-tenant model for provider instances and credentials:
///   • Each team can register its own provider instance for the same base URL with its
///     own OAuth client_id / secret (partial unique index is keyed on team_id).
///   • Credentials are per-user — two team members connecting the same provider get
///     independent Credential rows.
///   • ListCredentialsQuery returns ALL active credentials in the current team (any
///     owner), so the Add Repository flow can pick a teammate's credential when needed
///     (e.g. a shared service-account credential).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CrossTeamProviderInstanceTests
{
    private readonly PostgresFixture _fixture;

    public CrossTeamProviderInstanceTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Two_teams_can_each_register_GitHub_at_github_com_with_different_oauth_apps()
    {
        var teamA = await SeedTeamAsync("team-a").ConfigureAwait(false);
        var teamB = await SeedTeamAsync("team-b").ConfigureAwait(false);

        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamA, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new AddProviderInstanceCommand
            {
                Provider = ProviderKind.GitHub,
                DisplayName = "Acme GitHub",
                BaseUrl = "https://github.com",
                OauthClientId = "acme-client",
                OauthClientSecret = "acme-secret"
            }).ConfigureAwait(false);
        }

        // Same provider + same base URL in a DIFFERENT team — must NOT collide.
        using (var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamB, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            await mediator.Send(new AddProviderInstanceCommand
            {
                Provider = ProviderKind.GitHub,
                DisplayName = "Globex GitHub",
                BaseUrl = "https://github.com",
                OauthClientId = "globex-client",
                OauthClientSecret = "globex-secret"
            }).ConfigureAwait(false);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var rows = await db.ProviderInstance.AsNoTracking()
            .Where(p => p.BaseUrl == "https://github.com" && p.Provider == ProviderKind.GitHub && (p.TeamId == teamA || p.TeamId == teamB))
            .OrderBy(p => p.DisplayName)
            .ToListAsync().ConfigureAwait(false);

        rows.Count.ShouldBe(2);
        rows[0].DisplayName.ShouldBe("Acme GitHub");
        rows[0].OauthClientId.ShouldBe("acme-client");
        rows[1].DisplayName.ShouldBe("Globex GitHub");
        rows[1].OauthClientId.ShouldBe("globex-client");
    }

    [Fact]
    public async Task Credentials_are_per_user_independent_for_same_provider_instance()
    {
        // Each team member who clicks Connect gets their own Credential row keyed by
        // OwnerUserId. Both rows coexist on the same provider instance.
        var teamId = await SeedTeamAsync("team-c").ConfigureAwait(false);
        var instanceId = await SeedProviderAsync(teamId).ConfigureAwait(false);

        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        await SeedUserAsync(aliceId, teamId, "alice").ConfigureAwait(false);
        await SeedUserAsync(bobId, teamId, "bob").ConfigureAwait(false);

        await SeedCredentialAsync(teamId, instanceId, aliceId, "alice's GitHub").ConfigureAwait(false);
        await SeedCredentialAsync(teamId, instanceId, bobId, "bob's GitHub").ConfigureAwait(false);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var creds = await db.Credential.AsNoTracking()
            .Where(c => c.ProviderInstanceId == instanceId && c.DeletedDate == null)
            .OrderBy(c => c.DisplayName)
            .ToListAsync().ConfigureAwait(false);

        creds.Count.ShouldBe(2);
        creds[0].OwnerUserId.ShouldBe(aliceId);
        creds[1].OwnerUserId.ShouldBe(bobId);
    }

    [Fact]
    public async Task ListCredentials_returns_all_team_members_credentials_not_just_callers()
    {
        // The Add Repository flow surfaces every active credential in the team so a user
        // can pick a teammate's credential when needed. This test pins that behavior so a
        // future "show only mine" filter doesn't sneak in unannounced.
        var teamId = await SeedTeamAsync("team-d").ConfigureAwait(false);
        var instanceId = await SeedProviderAsync(teamId).ConfigureAwait(false);
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        await SeedUserAsync(aliceId, teamId, "alice").ConfigureAwait(false);
        await SeedUserAsync(bobId, teamId, "bob").ConfigureAwait(false);
        await SeedCredentialAsync(teamId, instanceId, aliceId, "alice's GitHub").ConfigureAwait(false);
        await SeedCredentialAsync(teamId, instanceId, bobId, "bob's GitHub").ConfigureAwait(false);

        // Query as alice — must still see bob's credential too.
        using var scope = _fixture.BeginScopeAs(aliceId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        var result = await mediator.Send(new ListCredentialsQuery()).ConfigureAwait(false);

        result.Count.ShouldBe(2);
        result.Select(c => c.OwnerUserId).ShouldContain(aliceId);
        result.Select(c => c.OwnerUserId).ShouldContain(bobId);
    }

    // ── seed helpers ────────────────────────────────────────────────────────────

    private async Task<Guid> SeedTeamAsync(string slugPrefix)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@x", Name = $"Owner-{suffix}" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"{slugPrefix}-{suffix}", Name = $"Team-{suffix}", OwnerUserId = owner.Id };

        db.User.Add(owner);
        db.Team.Add(team);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return team.Id;
    }

    private async Task<Guid> SeedProviderAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Provider = ProviderKind.GitHub,
            DisplayName = "GitHub",
            BaseUrl = "https://github.com",
            OauthClientId = "team-client",
            OauthClientSecretEnc = encryptor.Encrypt("team-secret")
        };
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return instance.Id;
    }

    private async Task SeedUserAsync(Guid userId, Guid teamId, string name)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
        db.User.Add(new User { Id = userId, Email = $"{name}-{suffix}@x", Name = name });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Member });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task SeedCredentialAsync(Guid teamId, Guid instanceId, Guid ownerUserId, string displayName)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<CodeSpace.Core.Services.Credentials.IPayloadEncryptor>();
        var serializer = scope.Resolve<CodeSpace.Core.Services.Credentials.ICredentialPayloadSerializer>();
        var payload = new CodeSpace.Messages.Credentials.OAuthPayload { AccessToken = "at", RefreshToken = "rt" };

        db.Credential.Add(new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            ProviderInstanceId = instanceId,
            OwnerUserId = ownerUserId,
            AuthType = AuthType.OAuth,
            DisplayName = displayName,
            EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)),
            Status = CredentialStatus.Active
        });
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
