using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Issues;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// End-to-end coverage of the issue READ path (<see cref="IIssueService.ListAsync"/> +
/// <see cref="IIssueService.GetCountsAsync"/>) on real Postgres via the test provider
/// (<c>ProviderKind.Git</c>). <c>TestRepositoryProvider.ListIssuesAsync</c> echoes the state filter +
/// page + perPage into the returned issue's <c>ExternalId</c> so each test asserts those flowed through
/// the service preflight unchanged. Mirrors <see cref="IssueCreateActorFlowTests"/> for the write half.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class IssueCatalogFlowTests
{
    private readonly PostgresFixture _fixture;

    public IssueCatalogFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(null, "All")]
    [InlineData(IssueState.Open, "Open")]
    [InlineData(IssueState.Closed, "Closed")]
    public async Task List_forwards_the_state_filter_and_paging_to_the_provider(IssueState? state, string expectedTag)
    {
        var seed = await SeedAsync();

        using var scope = _fixture.BeginScope();
        var issues = await scope.Resolve<IIssueService>().ListAsync(seed.RepositoryId, seed.TeamId, state, page: 2, perPage: 10, CancellationToken.None);

        issues.Count.ShouldBe(1);
        issues[0].ExternalId.ShouldBe($"{expectedTag}-p2-pp10",
            customMessage: "the state filter, page and perPage must reach the provider verbatim through the read preflight");
        if (state is { } s) issues[0].State.ShouldBe(s);
    }

    [Fact]
    public async Task Counts_flow_through_verbatim()
    {
        var seed = await SeedAsync();

        using var scope = _fixture.BeginScope();
        var counts = await scope.Resolve<IIssueService>().GetCountsAsync(seed.RepositoryId, seed.TeamId, CancellationToken.None);

        counts.Open.ShouldBe(3);
        counts.Closed.ShouldBe(5);
    }

    [Fact]
    public async Task A_repository_in_another_team_is_refused_fail_closed()
    {
        var seed = await SeedAsync();
        var otherTeam = Guid.NewGuid();

        // The repo belongs to seed.TeamId; listing AS otherTeam must NOT resolve it — the tenant filter
        // finds nothing and the service refuses with the same non-leaking "not found" as a missing repo.
        using var scope = _fixture.BeginScope();
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            scope.Resolve<IIssueService>().ListAsync(seed.RepositoryId, otherTeam, IssueState.Open, page: 1, perPage: 30, CancellationToken.None));

        ex.Message.ShouldContain("not found", customMessage: "a cross-team repo is indistinguishable from a missing one (no existence leak)");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task<SeedResult> SeedAsync()
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

        await db.SaveChangesAsync();

        return new SeedResult(team.Id, repo.Id);
    }

    private sealed record SeedResult(Guid TeamId, Guid RepositoryId);
}
