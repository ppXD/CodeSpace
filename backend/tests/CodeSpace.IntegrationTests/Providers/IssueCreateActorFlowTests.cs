using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Issues;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// End-to-end coverage of <see cref="IIssueService.CreateAsync"/> on real Postgres via the test provider
/// (<c>ProviderKind.Git</c>), which echoes the acting credential's id back as the created issue's
/// <c>ExternalId</c> so each test asserts WHICH credential created it. Covers the Model B attribution wiring
/// (actor / connection / unlinked) — identical to the PR-open path — plus the service-level title validation.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class IssueCreateActorFlowTests
{
    private readonly PostgresFixture _fixture;

    public IssueCreateActorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Actor_mode_creates_as_the_actors_own_credential()
    {
        var seed = await SeedAsync(linkActor: true);

        var issue = await CreateAsync(seed.RepositoryId, seed.TeamId, seed.UserId);

        issue.ExternalId.ShouldBe(seed.ActorCredentialId.ToString(),
            customMessage: "with a linked actor the issue must be created by the actor's OWN credential, not the connection credential");
        issue.Title.ShouldBe("Track this");
        issue.State.ShouldBe(IssueState.Open);
    }

    [Fact]
    public async Task Default_mode_creates_as_the_connection_credential()
    {
        var seed = await SeedAsync(linkActor: true);

        var issue = await CreateAsync(seed.RepositoryId, seed.TeamId, actorUserId: null);

        issue.ExternalId.ShouldBe(seed.ConnectionCredentialId.ToString(),
            customMessage: "no actorUserId → unchanged behaviour: the repo's connection credential creates the issue");
    }

    [Fact]
    public async Task Actor_mode_for_an_unlinked_user_throws_actor_identity_required()
    {
        var seed = await SeedAsync(linkActor: false);

        var ex = await Should.ThrowAsync<ActorIdentityRequiredException>(() => CreateAsync(seed.RepositoryId, seed.TeamId, seed.UserId));

        ex.ProviderKind.ShouldBe(ProviderKind.Git);
        ex.ProviderInstanceId.ShouldBe(seed.ProviderInstanceId);
    }

    [Fact]
    public async Task A_blank_title_is_rejected_before_the_provider_is_called()
    {
        var seed = await SeedAsync(linkActor: false);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => CreateAsync(seed.RepositoryId, seed.TeamId, actorUserId: null, title: "   "));

        ex.Message.ShouldContain("title");
    }

    [Fact]
    public async Task Labels_reach_the_provider()
    {
        var seed = await SeedAsync(linkActor: false);

        var issue = await CreateAsync(seed.RepositoryId, seed.TeamId, actorUserId: null, labels: new[] { "bug", "p1" });

        issue.Labels.Select(l => l.Name).ShouldBe(new[] { "bug", "p1" }, customMessage: "the test provider echoes the labels, proving the neutral input reaches it");
    }

    [Fact]
    public async Task A_repository_in_another_team_is_refused_fail_closed()
    {
        var seed = await SeedAsync(linkActor: true);
        var otherTeam = Guid.NewGuid();

        // The repo belongs to seed.TeamId; creating AS otherTeam must NOT resolve it — the tenant filter
        // finds nothing and the service refuses with the same non-leaking "not found" as a missing repo.
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => CreateAsync(seed.RepositoryId, otherTeam, seed.UserId));
        ex.Message.ShouldContain("not found", customMessage: "a cross-team repo is indistinguishable from a missing one (no existence leak)");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<RemoteIssue> CreateAsync(Guid repositoryId, Guid teamId, Guid? actorUserId, string title = "Track this", string[]? labels = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IIssueService>().CreateAsync(
            repositoryId,
            teamId,
            new CreateIssueInput { Title = title, Body = "body", Labels = labels ?? Array.Empty<string>() },
            actorUserId,
            CancellationToken.None);
    }

    private async Task<SeedResult> SeedAsync(bool linkActor)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();
        var serializer = scope.Resolve<ICredentialPayloadSerializer>();

        string Pat(string token) => encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = token }));
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var user = new User { Id = Guid.NewGuid(), Email = $"u-{suffix}@x", Name = "tester" };
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team", OwnerUserId = user.Id };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.Git, DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local", OauthClientId = "client", OauthClientSecretEnc = encryptor.Encrypt("secret")
        };
        var connection = new Credential
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService,
            AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = Pat("conn"), Status = CredentialStatus.Active
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = connection.Id,
            ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://git.local/acme/api", Status = RepositoryStatus.Active
        };

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(connection);
        db.Repository.Add(repo);

        var actorCredentialId = Guid.Empty;
        if (linkActor)
        {
            var actorCred = new Credential
            {
                Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, OwnerUserId = user.Id,
                Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = "actor", EncryptedPayload = Pat("actor"), Status = CredentialStatus.Active
            };
            db.Credential.Add(actorCred);
            db.UserProviderIdentity.Add(new UserProviderIdentity
            {
                Id = Guid.NewGuid(), UserId = user.Id, ProviderInstanceId = instance.Id, CredentialId = actorCred.Id,
                ProviderUserId = "42", ProviderUsername = "tester"
            });
            actorCredentialId = actorCred.Id;
        }

        await db.SaveChangesAsync();

        return new SeedResult(user.Id, team.Id, instance.Id, repo.Id, connection.Id, actorCredentialId);
    }

    private sealed record SeedResult(Guid UserId, Guid TeamId, Guid ProviderInstanceId, Guid RepositoryId, Guid ConnectionCredentialId, Guid ActorCredentialId);
}
