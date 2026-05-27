using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class DbContextRoundTripTests
{
    private readonly PostgresFixture _fixture;

    public DbContextRoundTripTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Full_entity_graph_round_trips_with_enums_and_arrays()
    {
        var suffix = Guid.NewGuid().ToString("N").Substring(0, 8);

        var ownerId = await CreateGraphAsync(suffix).ConfigureAwait(false);

        using var readScope = _fixture.BeginScope();
        var db = readScope.Resolve<CodeSpaceDbContext>();

        var team = await db.Team.AsNoTracking().SingleAsync(t => t.Slug == $"team-{suffix}").ConfigureAwait(false);
        team.OwnerUserId.ShouldBe(ownerId);

        var instance = await db.ProviderInstance.AsNoTracking().SingleAsync(p => p.TeamId == team.Id).ConfigureAwait(false);
        instance.Provider.ShouldBe(ProviderKind.GitLab);
        instance.OauthDefaultScopes.ShouldNotBeNull();
        instance.OauthDefaultScopes!.ShouldContain("api");
        instance.OauthDefaultScopes!.ShouldContain("read_user");

        var credential = await db.Credential.AsNoTracking().SingleAsync(c => c.ProviderInstanceId == instance.Id).ConfigureAwait(false);
        credential.AuthType.ShouldBe(AuthType.OAuth);
        credential.Status.ShouldBe(CredentialStatus.Active);
        credential.Scopes.ShouldNotBeNull();
        credential.Scopes!.ShouldContain("api");

        var repo = await db.Repository.AsNoTracking().SingleAsync(r => r.CredentialId == credential.Id).ConfigureAwait(false);
        repo.Visibility.ShouldBe(RepositoryVisibility.Private);
        repo.Status.ShouldBe(RepositoryStatus.Active);
        repo.DefaultBranch.ShouldBe("main");

        var webhook = await db.RepositoryWebhook.AsNoTracking().SingleAsync(w => w.RepositoryId == repo.Id).ConfigureAwait(false);
        webhook.SubscribedEvents.ShouldContain("push");
        webhook.SubscribedEvents.ShouldContain("merge_request");
        webhook.Active.ShouldBeTrue();
    }

    [Fact]
    public async Task DateTimeOffset_round_trips_with_utc_instant_preserved()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var originalInstant = new DateTimeOffset(2026, 5, 20, 14, 30, 0, TimeSpan.FromHours(8));
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"utc-{Guid.NewGuid():N}@test",
            Name = "UTC Round Trip",
            LastLoginDate = originalInstant
        };
        db.User.Add(user);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var reloaded = await db.User.AsNoTracking().SingleAsync(u => u.Id == user.Id).ConfigureAwait(false);
        reloaded.LastLoginDate.ShouldNotBeNull();
        reloaded.LastLoginDate!.Value.UtcDateTime.ShouldBe(originalInstant.UtcDateTime);
    }

    [Fact]
    public async Task Enum_team_role_persists_as_string()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var owner = new User { Id = Guid.NewGuid(), Email = $"role-{Guid.NewGuid():N}@test", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"role-team-{Guid.NewGuid():N}", Name = "Role Team", OwnerUserId = owner.Id };
        var membership = new TeamMembership { Id = Guid.NewGuid(), TeamId = team.Id, UserId = owner.Id, Role = TeamRole.Owner };

        db.User.Add(owner);
        db.Team.Add(team);
        db.TeamMembership.Add(membership);
        await db.SaveChangesAsync().ConfigureAwait(false);

        var reloaded = await db.TeamMembership.AsNoTracking().SingleAsync(m => m.Id == membership.Id).ConfigureAwait(false);
        reloaded.Role.ShouldBe(TeamRole.Owner);
    }

    private async Task<Guid> CreateGraphAsync(string suffix)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var owner = new User { Id = Guid.NewGuid(), Email = $"owner-{suffix}@test", Name = "Owner" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"team-{suffix}", Name = "Team", OwnerUserId = owner.Id };
        var project = TestProjectSeed.BuildDefaultProject(team.Id, owner.Id);

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Provider = ProviderKind.GitLab,
            DisplayName = "Self-hosted GitLab",
            BaseUrl = $"https://gitlab-{suffix}.example.com",
            OauthDefaultScopes = new List<string> { "api", "read_user" }
        };

        var credential = new Credential
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProviderInstanceId = instance.Id,
            AuthType = AuthType.OAuth,
            DisplayName = "Owner OAuth",
            EncryptedPayload = "encrypted-blob",
            Scopes = new List<string> { "api" },
            Status = CredentialStatus.Active
        };

        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            ProjectId = project.Id,
            ProviderInstanceId = instance.Id,
            CredentialId = credential.Id,
            ExternalId = $"42-{suffix}",
            NamespacePath = "acme",
            Name = "internal-bk",
            FullPath = "acme/internal-bk",
            WebUrl = $"https://gitlab-{suffix}.example.com/acme/internal-bk",
            Visibility = RepositoryVisibility.Private,
            Status = RepositoryStatus.Active
        };

        var webhook = new RepositoryWebhook
        {
            Id = Guid.NewGuid(),
            RepositoryId = repo.Id,
            ExternalId = $"wh-{suffix}",
            CallbackUrl = "https://codespace.local/webhooks/abc",
            SecretEnc = "secret-blob",
            SubscribedEvents = new List<string> { "push", "merge_request" }
        };

        db.User.Add(owner);
        db.Team.Add(team);
        db.Project.Add(project);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repo);
        db.RepositoryWebhook.Add(webhook);

        await db.SaveChangesAsync().ConfigureAwait(false);
        return owner.Id;
    }
}
