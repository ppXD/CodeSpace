using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Providers;

/// <summary>
/// <c>git.merge_pr</c> with <c>deleteSourceBranch</c>, taken from the node registry and run through
/// <c>IPullRequestService</c>, the provider registry, the real GitHub provider and Octokit, on real Postgres, against a
/// loopback GitHub. This is the chain an outsider's fork pull request reaches, whether a workflow merges it or an agent
/// tool does. The head branch is named <c>release</c>, like acme/api's own release branch: the pull request's branch only
/// when its head lives in acme/api.
///
/// <para>Fidelity: high for everything CodeSpace runs; GitHub is the loopback.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PullRequestMergeSourceBranchFlowTests
{
    private readonly PostgresFixture _fixture;

    public PullRequestMergeSourceBranchFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Theory]
    [InlineData(false, "Deleted", 1)]
    [InlineData(true, "SkippedFork", 0)]
    public async Task A_merged_pull_request_s_branch_is_deleted_only_in_its_own_repository(bool headInFork, string expectedOutcome, int expectedDeletes)
    {
        using var github = new StubProviderHost()
            .Answer("PUT", "/repos/acme/api/pulls/77/merge", 200, """{"sha":"9f8e7d6c5b4a","merged":true,"message":"Pull Request successfully merged"}""")
            .Answer("GET", "/repos/acme/api/pulls/77", 200, PullRequestJson(headInFork ? Outsider : AcmeApi))
            .Answer("DELETE", "/repos/acme/api/git/refs/heads/release", 204, string.Empty);

        var seed = await SeedGitHubRepositoryAsync(github.BaseUrl);

        using var scope = _fixture.BeginScope();
        var result = await scope.Resolve<INodeRegistry>().Resolve("git.merge_pr").RunAsync(Context(seed), CancellationToken.None);

        result.Status.ShouldBe(NodeStatus.Success, result.Error);
        result.Outputs["merged"].GetBoolean().ShouldBeTrue();
        result.Outputs["sourceBranchDeletion"].GetString().ShouldBe(expectedOutcome, result.Outputs["sourceBranchDetail"].GetString());
        github.Requests.Count(r => r.Method == "DELETE").ShouldBe(expectedDeletes, "acme/api's release is deleted only when it is the pull request's own head branch");
    }

    private static readonly object AcmeApi = new { id = 4242, name = "api", full_name = "acme/api", owner = new { login = "acme" } };

    private static readonly object Outsider = new { id = 9090, name = "api", full_name = "outsider/api", owner = new { login = "outsider" }, fork = true };

    private static string PullRequestJson(object headRepository) => JsonSerializer.Serialize(new
    {
        id = 7077,
        number = 77,
        title = "Release fixes",
        state = "closed",
        merged = true,
        merged_at = "2026-09-24T08:00:00Z",
        merge_commit_sha = "9f8e7d6c5b4a",
        head = new { @ref = "release", sha = "0a1b2c3d", repo = headRepository },
        @base = new { @ref = "main", sha = "4e5f6a7b", repo = AcmeApi },
        user = new { login = "outsider" },
        html_url = "https://github.test/acme/api/pull/77"
    });

    private static NodeRunContext Context(SeedResult seed) => new()
    {
        Inputs = new Dictionary<string, JsonElement>
        {
            ["repositoryId"] = JsonSerializer.SerializeToElement(seed.RepositoryId.ToString()),
            ["number"] = JsonSerializer.SerializeToElement(77),
            ["deleteSourceBranch"] = JsonSerializer.SerializeToElement(true),
        },
        Config = new Dictionary<string, JsonElement>(),
        RawInputs = JsonDocument.Parse("{}").RootElement,
        RawConfig = JsonDocument.Parse("{}").RootElement,
        Scope = new NodeRunScope { Trigger = new Dictionary<string, JsonElement>(), Sys = new Dictionary<string, JsonElement> { [SystemScopeKeys.TeamId] = JsonSerializer.SerializeToElement(seed.TeamId.ToString()) } },
        Logger = NullLogger.Instance,
        Observability = NodeObservability.NoOp,
    };

    private async Task<SeedResult> SeedGitHubRepositoryAsync(string baseUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var encryptor = scope.Resolve<IPayloadEncryptor>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"t-{suffix}", Name = "Team" };
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitHub, DisplayName = "loopback", BaseUrl = baseUrl, ApiUrl = baseUrl };
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.Pat, DisplayName = "connection",
            EncryptedPayload = encryptor.Encrypt(scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "fake-loopback-token" })), Status = CredentialStatus.Active
        };
        var repository = new Repository
        {
            Id = Guid.NewGuid(), TeamId = team.Id, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = "4242", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://github.test/acme/api", Status = RepositoryStatus.Active
        };

        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new SeedResult(team.Id, repository.Id);
    }

    private sealed record SeedResult(Guid TeamId, Guid RepositoryId);
}
