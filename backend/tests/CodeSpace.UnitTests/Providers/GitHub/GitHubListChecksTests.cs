using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.UnitTests.Providers.GitHub;

/// <summary>
/// The real <see cref="GitHubRepositoryProvider"/> reading a pull request's check runs — Octokit, the wire, the resilience
/// wrapper — against a loopback GitHub. Workflows gate merges on this read, so a read that fails has to fail. An empty
/// list means only that GitHub answered "no check runs on the head commit". A refused token, a rate limit or a dropped
/// connection is not an empty list, and neither is a pull request whose head commit GitHub did not report.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitHubListChecksTests : IDisposable
{
    private const string PullPath = "/repos/acme/api/pulls/7";
    private const string CheckRunsPath = "/repos/acme/api/commits/0a1b2c3d4e5f/check-runs";

    private readonly StubProviderHost _github = new();

    public void Dispose() => _github.Dispose();

    [Theory]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(404, 1)]
    [InlineData(429, 1)]
    [InlineData(503, ExternalCallResilience.MaxAttempts)]
    public async Task A_check_runs_read_GitHub_refuses_fails_with_its_status(int status, int expectedAttempts)
    {
        _github.Answer("GET", PullPath, 200, PullRequestJson(head: new { @ref = "feature/ci", sha = "0a1b2c3d4e5f" })).Answer("GET", CheckRunsPath, status, $$"""{"message":"{{status}}"}""");

        var thrown = await Should.ThrowAsync<ProviderApiException>(ListChecksAsync);

        thrown.StatusCode.ShouldBe(status);
        _github.Sent("GET", CheckRunsPath).ShouldBe(expectedAttempts, "a 5xx is retried; any other refusal is the answer");
    }

    [Fact]
    public async Task A_dropped_connection_is_retried_then_fails_the_read()
    {
        _github.Answer("GET", PullPath, 200, PullRequestJson(head: new { @ref = "feature/ci", sha = "0a1b2c3d4e5f" })).Answer("GET", CheckRunsPath, _ => StubReply.DropConnection);

        await Should.ThrowAsync<HttpRequestException>(ListChecksAsync);

        _github.Sent("GET", CheckRunsPath).ShouldBe(ExternalCallResilience.MaxAttempts, "a dropped connection is transient — retried, then the read fails rather than reading as no checks");
    }

    [Fact]
    public async Task A_pull_request_without_a_head_commit_fails_the_read()
    {
        _github.Answer("GET", PullPath, 200, PullRequestJson(head: null));

        await Should.ThrowAsync<InvalidOperationException>(ListChecksAsync);

        _github.Sent("GET", "/check-runs").ShouldBe(0);
    }

    [Fact]
    public async Task A_head_commit_with_no_check_runs_has_no_checks()
    {
        _github.Answer("GET", PullPath, 200, PullRequestJson(head: new { @ref = "feature/ci", sha = "0a1b2c3d4e5f" })).Answer("GET", CheckRunsPath, 200, """{"total_count":0,"check_runs":[]}""");

        var checks = await ListChecksAsync();

        checks.ShouldBeEmpty("GitHub answered that no check ran on the head commit — the one empty list this read may return");
    }

    private Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync() => Provider().ListChecksAsync(Context(), Repository, 7, CancellationToken.None);

    private static string PullRequestJson(object? head) => JsonSerializer.Serialize(new
    {
        id = 7007,
        number = 7,
        title = "Gate on CI",
        state = "open",
        head,
        @base = new { @ref = "main", sha = "4e5f6a7b8c9d" },
        user = new { login = "codespace-bot" },
        html_url = "https://github.test/acme/api/pull/7"
    });

    private static readonly RemoteRepository Repository = new()
    {
        ExternalId = "4242",
        NamespacePath = "acme",
        Name = "api",
        FullPath = "acme/api",
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = "https://github.test/acme/api"
    };

    private ProviderContext Context() => new(new ProviderInstance { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = ProviderKind.GitHub, DisplayName = "loopback", BaseUrl = _github.BaseUrl, ApiUrl = _github.BaseUrl }, new Credential { Id = Guid.NewGuid(), AuthType = AuthType.Pat, DisplayName = "pat", EncryptedPayload = "unused" });

    private static GitHubRepositoryProvider Provider()
    {
        var resilience = new ExternalCallResilience(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitHubErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);
        var normalizer = new GitHubEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>()));

        return new GitHubRepositoryProvider(new StaticTokenAuth(), resilience, new GitHubSignatureVerifier(), normalizer, new GitHubWebhookRepositoryIdentifier());
    }

    private sealed class StaticTokenAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "ghp_loopback" });
    }
}
