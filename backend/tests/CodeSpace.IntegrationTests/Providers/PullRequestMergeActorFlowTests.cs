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
/// End-to-end coverage of <see cref="IPullRequestService.MergePullRequestAsync"/> on real Postgres via the
/// test provider (<c>ProviderKind.Git</c>), which echoes the acting credential's id back as the merge result's
/// <c>Sha</c> so each test asserts WHICH credential merged. Covers the Model B attribution wiring
/// (actor / connection / unlinked) — identical to open + review — plus that the neutral merge input
/// (method, delete-source-branch) reaches the provider.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PullRequestMergeActorFlowTests
{
    private readonly PostgresFixture _fixture;

    public PullRequestMergeActorFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Actor_mode_merges_as_the_actors_own_credential()
    {
        var seed = await SeedAsync(linkActor: true);

        var result = await MergeAsync(seed.RepositoryId, seed.UserId);

        result.Merged.ShouldBeTrue();
        result.Sha.ShouldBe(seed.ActorCredentialId.ToString(),
            customMessage: "with a linked actor the merge must run under the actor's OWN credential, not the connection credential");
    }

    [Fact]
    public async Task Default_mode_merges_as_the_connection_credential()
    {
        var seed = await SeedAsync(linkActor: true);

        var result = await MergeAsync(seed.RepositoryId, actorUserId: null);

        result.Sha.ShouldBe(seed.ConnectionCredentialId.ToString(),
            customMessage: "no actorUserId → unchanged behaviour: the repo's connection credential merges");
    }

    [Fact]
    public async Task Actor_mode_for_an_unlinked_user_throws_actor_identity_required()
    {
        var seed = await SeedAsync(linkActor: false);

        var ex = await Should.ThrowAsync<ActorIdentityRequiredException>(() => MergeAsync(seed.RepositoryId, seed.UserId));

        ex.ProviderKind.ShouldBe(ProviderKind.Git);
        ex.ProviderInstanceId.ShouldBe(seed.ProviderInstanceId);
    }

    [Fact]
    public async Task The_merge_method_reaches_the_provider()
    {
        var seed = await SeedAsync(linkActor: false);

        var result = await MergeAsync(seed.RepositoryId, actorUserId: null, method: PullRequestMergeMethod.Squash);

        result.Message.ShouldContain("Squash", customMessage: "the test provider echoes the chosen method, proving the neutral input reaches it");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<RemotePullRequestMergeResult> MergeAsync(Guid repositoryId, Guid? actorUserId, int number = 777, PullRequestMergeMethod method = PullRequestMergeMethod.Merge)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IPullRequestService>().MergePullRequestAsync(
            repositoryId,
            number,
            new MergePullRequestInput { Method = method, DeleteSourceBranch = true },
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
