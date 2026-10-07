using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// The real <see cref="GitLabRepositoryProvider"/> reading a merge request's CI — NGitLab, the wire, the resilience
/// wrapper — against a loopback GitLab. Workflows gate merges on this read, so a read that fails has to fail. A rate
/// limit, an outage, a refused token, a dropped connection or a payload NGitLab cannot parse is not an empty list. GitLab
/// answers the merge request's pipeline list with [] both when no pipeline ran and when this credential may read none of
/// them, so an empty list stands only once GitLab confirms it. The latest pipeline's own status is a floor under its jobs,
/// and only a pipeline GitLab calls success passes: one that failed, is blocked on a manual job, is waiting on a delayed
/// job or was skipped never reads green because its job list looks clean.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitLabListChecksTests : IDisposable
{
    private const string PipelinesPath = "/api/v4/projects/4242/merge_requests/7/pipelines";
    private const string JobsPath = "/api/v4/projects/4242/pipelines/77/jobs";
    private const string ProjectPipelinesPath = "/api/v4/projects/4242/pipelines?";
    private const string MergeRequestPath = "/api/v4/projects/4242/merge_requests/7?";
    private const string ProjectPath = "/api/v4/projects/4242";

    private readonly StubProviderHost _gitlab = new();

    public void Dispose() => _gitlab.Dispose();

    [Theory]
    [InlineData(429, 1)]
    [InlineData(503, ExternalCallResilience.MaxAttempts)]
    [InlineData(403, 1)]
    [InlineData(404, 1)]
    public async Task A_pipelines_read_GitLab_refuses_fails_with_its_status(int status, int expectedAttempts)
    {
        _gitlab.Answer("GET", PipelinesPath, status, $$"""{"message":"{{status}}"}""");

        var thrown = await Should.ThrowAsync<ProviderApiException>(ListChecksAsync);

        thrown.StatusCode.ShouldBe(status);
        _gitlab.Sent("GET", PipelinesPath).ShouldBe(expectedAttempts, "a 5xx is retried; any other refusal is the answer");
    }

    [Fact]
    public async Task A_dropped_connection_is_retried_then_fails_the_read()
    {
        // NGitLab reads the answer through HttpWebRequest: a body cut short surfaces as an HttpIOException, not the
        // HttpRequestException an HttpClient SDK throws. It is the same network blip, so it gets the same retries.
        _gitlab.Answer("GET", PipelinesPath, _ => StubReply.DropConnection);

        await Should.ThrowAsync<HttpIOException>(ListChecksAsync);

        _gitlab.Sent("GET", PipelinesPath).ShouldBe(ExternalCallResilience.MaxAttempts, "a dropped connection is transient — one TCP reset must not fail the gate run");
    }

    [Fact]
    public async Task A_refused_connection_is_retried_then_fails_the_read()
    {
        // Nothing listens on the port, so no attempt can be counted on the wire — the backoff between attempts is the
        // evidence they happened. NGitLab reports a connection it never got an answer on as a WebException without one.
        var stopwatch = Stopwatch.StartNew();

        await Should.ThrowAsync<WebException>(() => Provider().ListChecksAsync(Context($"http://127.0.0.1:{UnusedLoopbackPort()}"), Repository, 7, CancellationToken.None));

        var retriedBackoff = ExternalCallResilience.ComputeBackoff(1) + ExternalCallResilience.ComputeBackoff(2);
        stopwatch.Elapsed.ShouldBeGreaterThanOrEqualTo(retriedBackoff, $"a refused connection must be tried {ExternalCallResilience.MaxAttempts} times, with {retriedBackoff.TotalMilliseconds}ms of backoff between the attempts — check ExternalCallResilience.IsTransient for WebException");
    }

    [Fact]
    public async Task A_failed_pipeline_whose_jobs_cannot_be_read_fails_the_read()
    {
        _gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson("failed")).Answer("GET", JobsPath, 429, """{"message":"429 Too Many Requests"}""");

        var thrown = await Should.ThrowAsync<ProviderApiException>(ListChecksAsync);

        thrown.StatusCode.ShouldBe(429);
    }

    [Fact]
    public async Task A_status_NGitLab_cannot_parse_fails_the_read()
    {
        // GitLab added waiting_for_callback after NGitLab 11.7's JobStatus; the parse throws rather than guess.
        _gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson("waiting_for_callback"));

        await Should.ThrowAsync<Exception>(ListChecksAsync);
    }

    [Fact]
    public async Task The_checks_come_from_the_newest_pipeline()
    {
        // GitLab lists a merge request's pipelines newest-first. The older one went green; the push after it went red.
        _gitlab.Answer("GET", PipelinesPath, 200, $"[{PipelineJson(78, "failed")},{PipelineJson(77, "success")}]")
            .Answer("GET", "/api/v4/projects/4242/pipelines/78/jobs", 200, JobsJson(new[] { "success", "failed" }))
            .Answer("GET", JobsPath, 200, JobsJson(new[] { "success", "success" }));

        var checks = await ListChecksAsync();

        GitFetchPrChecksNode.SummarizeChecks(checks).State.ShouldBe("failure", "an older green pipeline must not stand in for the newest red one");
        _gitlab.Sent("GET", "/api/v4/projects/4242/pipelines/78/jobs").ShouldBe(1);
        _gitlab.Sent("GET", JobsPath).ShouldBe(0);
    }

    [Fact]
    public async Task A_merge_request_with_no_pipeline_has_no_checks()
    {
        _gitlab.Answer("GET", PipelinesPath, 200, "[]");
        AnswerNoPipeline();

        var checks = await ListChecksAsync();

        checks.ShouldBeEmpty("GitLab answered that no pipeline ran, and the credential may read the project's pipelines — the one empty list this read may return");
        _gitlab.Sent("GET", ProjectPipelinesPath).ShouldBe(1);
        _gitlab.Sent("GET", MergeRequestPath).ShouldBe(1);
    }

    [Fact]
    public async Task A_project_with_CI_turned_off_has_no_checks()
    {
        // No pipeline can run, and GitLab refuses every pipeline read on such a project — so it is not asked.
        _gitlab.Answer("GET", PipelinesPath, 200, "[]");
        AnswerNoPipeline(buildsAccessLevel: "disabled", projectPipelinesStatus: 403);

        var checks = await ListChecksAsync();

        checks.ShouldBeEmpty();
        _gitlab.Sent("GET", ProjectPipelinesPath).ShouldBe(0);
    }

    [Fact]
    public async Task An_empty_pipeline_list_from_a_credential_that_may_not_read_pipelines_fails_the_read()
    {
        // CI is visible to project members only and the connection's identity is not one: the merge request's list hides
        // every pipeline behind 200 [], and the project's list refuses the same credential with 403.
        _gitlab.Answer("GET", PipelinesPath, 200, "[]");
        AnswerNoPipeline(projectPipelinesStatus: 403);

        var thrown = await Should.ThrowAsync<ProviderApiException>(ListChecksAsync);

        thrown.StatusCode.ShouldBe(403, "a credential that cannot see the pipelines cannot vouch that none ran");
    }

    [Fact]
    public async Task An_empty_pipeline_list_beside_a_head_pipeline_fails_the_read()
    {
        // A merge request from a fork whose pipelines this credential may not read: its list shows only the target
        // project's pipelines (none), while the merge request still names the fork's red head pipeline.
        _gitlab.Answer("GET", PipelinesPath, 200, "[]");
        AnswerNoPipeline(headPipelineStatus: "failed");

        var thrown = await Should.ThrowAsync<InvalidOperationException>(ListChecksAsync);

        thrown.Message.ShouldContain("head pipeline");
    }

    [Theory]
    [InlineData("failed", "failed", "Failure")]
    [InlineData("success", "success", "Success")]
    [InlineData("running", "success,running", "Pending,Success")]
    [InlineData("running", "success", "Success,pipeline=Pending")]
    [InlineData("failed", "success,success", "Success,Success,pipeline=Failure")]
    [InlineData("canceled", "success,skipped", "Skipped,Success,pipeline=Cancelled")]
    [InlineData("manual", "success,manual", "Skipped,Success,pipeline=Pending")]       // blocked: a manual job holds the pipeline
    [InlineData("scheduled", "success,scheduled", "Skipped,Success,pipeline=Pending")] // a delayed job has not run yet
    [InlineData("skipped", "skipped", "Skipped,pipeline=Cancelled")]                   // nothing ran; GitLab's merge check does not count it as success
    public async Task The_pipeline_status_is_a_floor_under_its_jobs(string pipelineStatus, string jobStatuses, string expectedStatuses)
    {
        _gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson(pipelineStatus)).Answer("GET", JobsPath, 200, JobsJson(jobStatuses.Split(',')));

        var checks = await ListChecksAsync();

        var statuses = checks.Select(c => c.Name == "pipeline" ? $"pipeline={c.Status}" : c.Status.ToString()).Order(StringComparer.Ordinal);

        string.Join(",", statuses).ShouldBe(expectedStatuses, "the pipeline joins its jobs only when no job already carries its verdict");
        checks.Where(c => c.Name == "pipeline").ShouldAllBe(c => c.DetailsUrl == "https://gitlab.test/acme/api/-/pipelines/77");
    }

    [Theory]
    [InlineData("success", true)]
    [InlineData("failed", false)]
    [InlineData("canceled", false)]
    [InlineData("canceling", false)]
    [InlineData("skipped", false)]
    [InlineData("created", false)]
    [InlineData("waiting_for_resource", false)]
    [InlineData("preparing", false)]
    [InlineData("pending", false)]
    [InlineData("running", false)]
    [InlineData("manual", false)]
    [InlineData("scheduled", false)]
    public async Task Only_a_pipeline_GitLab_calls_success_passes_the_gate(string pipelineStatus, bool passes)
    {
        _gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson(pipelineStatus)).Answer("GET", JobsPath, 200, JobsJson(new[] { "success" }));

        var checks = await ListChecksAsync();

        GitFetchPrChecksNode.SummarizeChecks(checks).AllPassed.ShouldBe(passes, $"a pipeline GitLab calls {pipelineStatus} over one green job");
    }

    private Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync() => Provider().ListChecksAsync(Context(_gitlab.BaseUrl), Repository, 7, CancellationToken.None);

    /// <summary>
    /// What GitLab says once the merge request lists no pipeline: the project's CI setting, the project's own pipeline list
    /// (403 when the credential may not read pipelines) and the merge request's head pipeline. Registered after every
    /// narrower route — the project's path is a prefix of all of them — and answering only its exact path, so a route the
    /// test did not stub still reads as 501.
    /// </summary>
    private void AnswerNoPipeline(string buildsAccessLevel = "enabled", int projectPipelinesStatus = 200, string? headPipelineStatus = null)
    {
        _gitlab.Answer("GET", ProjectPipelinesPath, projectPipelinesStatus, projectPipelinesStatus == 200 ? "[]" : $$"""{"message":"{{projectPipelinesStatus}} Forbidden"}""")
            .Answer("GET", MergeRequestPath, 200, MergeRequestJson(headPipelineStatus))
            .Answer("GET", ProjectPath, request => request.PathAndQuery == ProjectPath ? new StubReply(200, ProjectJson(buildsAccessLevel)) : new StubReply(501, """{"message":"no stub configured for this route"}"""));
    }

    private static string PipelinesJson(string status) => $"[{PipelineJson(77, status)}]";

    private static string PipelineJson(long id, string status) =>
        $$"""{"id":{{id}},"iid":3,"project_id":4242,"status":"{{status}}","ref":"feature/ci","sha":"0123456789abcdef0123456789abcdef01234567","web_url":"https://gitlab.test/acme/api/-/pipelines/{{id}}"}""";

    private static string JobsJson(IEnumerable<string> statuses) =>
        "[" + string.Join(",", statuses.Select((status, i) => $$"""{"id":{{i + 1}},"name":"job-{{i + 1}}","stage":"test","status":"{{status}}","web_url":"https://gitlab.test/acme/api/-/jobs/{{i + 1}}"}""")) + "]";

    private static string MergeRequestJson(string? headPipelineStatus) =>
        $$"""{"id":7007,"iid":7,"project_id":4242,"title":"Gate on CI","state":"opened","source_branch":"feature/ci","target_branch":"main","sha":"0123456789abcdef0123456789abcdef01234567","head_pipeline":{{(headPipelineStatus == null ? "null" : PipelineJson(91, headPipelineStatus))}},"web_url":"https://gitlab.test/acme/api/-/merge_requests/7"}""";

    private static string ProjectJson(string buildsAccessLevel) =>
        $$"""{"id":4242,"name":"api","path":"api","path_with_namespace":"acme/api","default_branch":"main","builds_access_level":"{{buildsAccessLevel}}","web_url":"https://gitlab.test/acme/api"}""";

    private static int UnusedLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static readonly RemoteRepository Repository = new()
    {
        ExternalId = "4242",
        NamespacePath = "acme",
        Name = "api",
        FullPath = "acme/api",
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = "https://gitlab.test/acme/api"
    };

    private static ProviderContext Context(string baseUrl) => new(new ProviderInstance { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = ProviderKind.GitLab, DisplayName = "loopback", BaseUrl = baseUrl }, new Credential { Id = Guid.NewGuid(), AuthType = AuthType.Pat, DisplayName = "pat", EncryptedPayload = "unused" });

    private static GitLabRepositoryProvider Provider()
    {
        var resilience = new ExternalCallResilience(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitLabErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);
        var normalizer = new GitLabEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>()));

        return new GitLabRepositoryProvider(new StaticTokenAuth(), resilience, new GitLabSignatureVerifier(), normalizer, new GitLabWebhookRepositoryIdentifier());
    }

    private sealed class StaticTokenAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "glpat-loopback" });
    }
}
