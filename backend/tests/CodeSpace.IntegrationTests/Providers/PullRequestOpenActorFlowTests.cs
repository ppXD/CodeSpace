using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// End-to-end coverage of <see cref="IPullRequestService.OpenPullRequestAsync"/> on real Postgres via the
/// test provider (<c>ProviderKind.Git</c>), which echoes the acting credential's id back as the created PR's
/// <c>ExternalId</c> so each test asserts WHICH credential opened it. Covers the Model B attribution wiring
/// (actor / connection / unlinked) — identical to the review write-back — plus the service-level input
/// validation new to this path (title + distinct branches required).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PullRequestOpenActorFlowTests
{
    private readonly PostgresFixture _fixture;

    public PullRequestOpenActorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Actor_mode_opens_as_the_actors_own_credential()
    {
        var seed = await SeedAsync(linkActor: true);

        var pr = await OpenAsync(seed.RepositoryId, seed.UserId);

        pr.ExternalId.ShouldBe(seed.ActorCredentialId.ToString(),
            customMessage: "with a linked actor the PR must be opened by the actor's OWN credential, not the connection credential");
        pr.SourceBranch.ShouldBe("feature");
        pr.TargetBranch.ShouldBe("main");
    }

    [Fact]
    public async Task Default_mode_opens_as_the_connection_credential()
    {
        var seed = await SeedAsync(linkActor: true);

        var pr = await OpenAsync(seed.RepositoryId, actorUserId: null);

        pr.ExternalId.ShouldBe(seed.ConnectionCredentialId.ToString(),
            customMessage: "no actorUserId → unchanged behaviour: the repo's connection credential opens the PR");
    }

    [Fact]
    public async Task Actor_mode_for_an_unlinked_user_throws_actor_identity_required()
    {
        var seed = await SeedAsync(linkActor: false);

        var ex = await Should.ThrowAsync<ActorIdentityRequiredException>(() => OpenAsync(seed.RepositoryId, seed.UserId));

        ex.ProviderKind.ShouldBe(ProviderKind.Git);
        ex.ProviderInstanceId.ShouldBe(seed.ProviderInstanceId);
    }

    [Fact]
    public async Task A_missing_title_is_rejected_before_the_provider_is_called()
    {
        var seed = await SeedAsync(linkActor: false);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            OpenAsync(seed.RepositoryId, actorUserId: null, title: "   "));

        ex.Message.ShouldContain("title");
    }

    [Fact]
    public async Task Identical_source_and_target_branch_is_rejected()
    {
        var seed = await SeedAsync(linkActor: false);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            OpenAsync(seed.RepositoryId, actorUserId: null, source: "main", target: "main"));

        ex.Message.ShouldContain("differ");
    }

    [Fact]
    public async Task A_missing_branch_is_rejected()
    {
        var seed = await SeedAsync(linkActor: false);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            OpenAsync(seed.RepositoryId, actorUserId: null, source: ""));

        ex.Message.ShouldContain("branch");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<RemotePullRequest> OpenAsync(Guid repositoryId, Guid? actorUserId, string title = "Add feature", string source = "feature", string target = "main")
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IPullRequestService>().OpenPullRequestAsync(
            repositoryId,
            new OpenPullRequestInput { Title = title, SourceBranch = source, TargetBranch = target, Body = "body" },
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

        return new SeedResult(user.Id, instance.Id, repo.Id, connection.Id, actorCredentialId);
    }

    private sealed record SeedResult(Guid UserId, Guid ProviderInstanceId, Guid RepositoryId, Guid ConnectionCredentialId, Guid ActorCredentialId);
}
