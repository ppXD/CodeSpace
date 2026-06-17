using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// End-to-end coverage of <see cref="IChangeSetService.OpenPullRequestsAsync"/> on real Postgres via the test provider
/// (<c>ProviderKind.Git</c>): a multi-repo Change Set opens one PR PER REPO, each through the real team-scoped
/// <see cref="IPullRequestService"/> (so the per-repo repo+credential resolution + fail-closed tenancy is exercised),
/// with per-repo failure isolation + the no-branch skip.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ChangeSetServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public ChangeSetServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Opens_one_pr_per_repo_team_scoped()
    {
        var seed = await SeedAsync("web", "api");

        var result = await OpenAsync(seed.TeamId,
            (seed.Repos["web"], "feature", "main"),
            (seed.Repos["api"], "feature", "main"));

        result.OpenedCount.ShouldBe(2);
        result.FailedCount.ShouldBe(0);
        result.PullRequests.ShouldAllBe(p => p.Disposition == ChangeSetPullRequestDisposition.Opened);
        result.PullRequests.Select(p => p.RepositoryId).ShouldBe(new[] { seed.Repos["web"], seed.Repos["api"] }, ignoreOrder: true);
        result.PullRequests.ShouldAllBe(p => p.Number != null && p.Url != null);
    }

    [Fact]
    public async Task One_repos_failure_is_isolated_the_other_still_opens()
    {
        // api's source == target → the real service rejects it (branches must differ); web still opens. The set never aborts.
        var seed = await SeedAsync("web", "api");

        var result = await OpenAsync(seed.TeamId,
            (seed.Repos["web"], "feature", "main"),
            (seed.Repos["api"], "main", "main"));

        result.OpenedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(1);
        result.PullRequests.Single(p => p.RepositoryId == seed.Repos["web"]).Disposition.ShouldBe(ChangeSetPullRequestDisposition.Opened);

        var api = result.PullRequests.Single(p => p.RepositoryId == seed.Repos["api"]);
        api.Disposition.ShouldBe(ChangeSetPullRequestDisposition.Failed);
        api.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_no_branch_repo_is_skipped_and_a_cross_team_repo_fails_closed()
    {
        var seed = await SeedAsync("web", "api");

        var result = await OpenAsync(seed.TeamId,
            (seed.Repos["web"], "", "main"),                 // no produced branch → Skipped (never hits the provider)
            (seed.Repos["api"], "feature", "main"));

        result.SkippedCount.ShouldBe(1);
        result.OpenedCount.ShouldBe(1);
        result.PullRequests.Single(p => p.RepositoryId == seed.Repos["web"]).Disposition.ShouldBe(ChangeSetPullRequestDisposition.Skipped);

        // The SAME set opened under another team must fail-close every repo (the tenant filter finds nothing).
        var crossTeam = await OpenAsync(Guid.NewGuid(), (seed.Repos["api"], "feature", "main"));
        crossTeam.FailedCount.ShouldBe(1, "a cross-team repo resolves to nothing → Failed, never opened");
        crossTeam.OpenedCount.ShouldBe(0);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<ChangeSetResult> OpenAsync(Guid teamId, params (Guid repo, string source, string target)[] repos)
    {
        using var scope = _fixture.BeginScope();
        var spec = new ChangeSetPullRequestSpec
        {
            Title = "Coordinated change",
            Body = "body",
            Repositories = repos.Select(r => new ChangeSetPullRequest { RepositoryId = r.repo, SourceBranch = r.source, TargetBranch = r.target }).ToList(),
        };
        return await scope.Resolve<IChangeSetService>().OpenPullRequestsAsync(teamId, spec, actorUserId: null, CancellationToken.None);
    }

    private async Task<SeedResult> SeedAsync(params string[] repoNames)
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

        db.User.Add(user);
        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(connection);

        var repos = new Dictionary<string, Guid>();
        foreach (var name in repoNames)
        {
            var repo = new Repository
            {
                Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = connection.Id,
                ExternalId = $"ext-{suffix}-{name}", NamespacePath = "acme", Name = name, FullPath = $"acme/{name}",
                DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"https://git.local/acme/{name}", Status = RepositoryStatus.Active
            };
            db.Repository.Add(repo);
            repos[name] = repo.Id;
        }

        await db.SaveChangesAsync();

        return new SeedResult(team.Id, repos);
    }

    private sealed record SeedResult(Guid TeamId, IReadOnlyDictionary<string, Guid> Repos);
}
