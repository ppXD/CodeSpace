using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The CI gate <c>git.fetch_pr_checks</c> advertises, built the way its manifest says to build it and run by the real
/// engine against a loopback GitLab: trigger → <c>git.fetch_pr_checks</c> → <c>logic.if</c> on <c>allPassed</c> →
/// <c>git.merge_pr</c>. Everything between the workflow definition and the wire is production code — the engine, the
/// node, <c>PullRequestService</c> over Postgres, the container's provider and resilience wrapper, NGitLab.
///
/// <para>A checks read GitLab does not answer has to stop the run before the merge. Read as "no checks", it reaches
/// the merge with <c>allPassed = true</c> and merges a merge request whose CI nobody saw. An empty pipeline list the
/// credential cannot vouch for is such a read. And only a pipeline GitLab calls success opens the gate: one blocked on a
/// manual job, waiting on a delayed one or skipped is held like a failed one.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class PullRequestChecksGateFlowTests
{
    private const string PipelinesPath = "/api/v4/projects/4242/merge_requests/7/pipelines";
    private const string JobsPath = "/api/v4/projects/4242/pipelines/77/jobs";
    private const string MergePath = "/api/v4/projects/4242/merge_requests/7/merge";
    private const string ProjectPipelinesPath = "/api/v4/projects/4242/pipelines?";
    private const string MergeRequestPath = "/api/v4/projects/4242/merge_requests/7?";
    private const string ProjectPath = "/api/v4/projects/4242";

    private readonly PostgresFixture _fixture;

    public PullRequestChecksGateFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>What the loopback GitLab answers about merge request !7's CI.</summary>
    public enum CiAnswer
    {
        PipelinesRateLimited,
        PipelinesUnavailable,
        PipelinesForbidden,
        PipelinesNotFound,
        PipelinesConnectionDropped,
        FailedPipelineJobsRateLimited,
        PipelinesHiddenFromCredential,
        ForkHeadPipelineHidden,
        NoPipeline,
        GreenPipeline,
        FailedJob,
        FailedPipelineGreenJobs,
        BlockedPipeline,
        DelayedPipeline,
        SkippedPipeline,
        NewestPipelineFailed
    }

    [Theory]
    [InlineData(CiAnswer.PipelinesRateLimited)]
    [InlineData(CiAnswer.PipelinesUnavailable)]
    [InlineData(CiAnswer.PipelinesForbidden)]
    [InlineData(CiAnswer.PipelinesNotFound)]
    [InlineData(CiAnswer.PipelinesConnectionDropped)]
    [InlineData(CiAnswer.FailedPipelineJobsRateLimited)]
    [InlineData(CiAnswer.PipelinesHiddenFromCredential)]
    [InlineData(CiAnswer.ForkHeadPipelineHidden)]
    public async Task A_checks_read_GitLab_does_not_answer_fails_the_run_before_the_merge(CiAnswer answer)
    {
        using var gitlab = GitLabAnswering(answer);

        var (runId, nodes) = await RunGateAsync(gitlab.BaseUrl);

        nodes["checks"].Status.ShouldBe(NodeStatus.Failure, $"GitLab never told the checks node what CI said ({answer}); error={nodes["checks"].Error}");
        nodes.Values.Where(n => n.Status == NodeStatus.Success).Select(n => n.NodeId).ShouldBe(new[] { "start" }, "an unread checks list must leave the gate nothing to branch on");
        MergesSent(gitlab).ShouldBe(0, "a merge request whose CI was never read must not be merged");
        (await LoadRunAsync(runId)).Status.ShouldBe(WorkflowRunStatus.Failure);
    }

    [Theory]
    [InlineData(CiAnswer.GreenPipeline, true)]
    [InlineData(CiAnswer.NoPipeline, true)]
    [InlineData(CiAnswer.FailedJob, false)]
    [InlineData(CiAnswer.FailedPipelineGreenJobs, false)]
    [InlineData(CiAnswer.BlockedPipeline, false)]
    [InlineData(CiAnswer.DelayedPipeline, false)]
    [InlineData(CiAnswer.SkippedPipeline, false)]
    [InlineData(CiAnswer.NewestPipelineFailed, false)]
    public async Task The_gate_merges_only_a_merge_request_whose_CI_passed(CiAnswer answer, bool merges)
    {
        using var gitlab = GitLabAnswering(answer);

        var (runId, nodes) = await RunGateAsync(gitlab.BaseUrl);

        nodes["checks"].Status.ShouldBe(NodeStatus.Success, $"error={nodes["checks"].Error}");
        nodes["merge"].Status.ShouldBe(merges ? NodeStatus.Success : NodeStatus.Skipped, $"{answer}: allPassed={JsonDocument.Parse(nodes["checks"].OutputsJson).RootElement.GetProperty("allPassed")}");
        MergesSent(gitlab).ShouldBe(merges ? 1 : 0);
        (await LoadRunAsync(runId)).Status.ShouldBe(WorkflowRunStatus.Success);
    }

    private static int MergesSent(StubProviderHost gitlab) => gitlab.Requests.Count(r => r.Method == "PUT" && r.PathAndQuery.Contains(MergePath, StringComparison.Ordinal));

    private static StubProviderHost GitLabAnswering(CiAnswer answer)
    {
        var gitlab = new StubProviderHost().Answer("PUT", MergePath, 200, MergedMergeRequestJson);

        return answer switch
        {
            CiAnswer.PipelinesRateLimited => gitlab.Answer("GET", PipelinesPath, 429, """{"message":"429 Too Many Requests"}"""),
            CiAnswer.PipelinesUnavailable => gitlab.Answer("GET", PipelinesPath, 503, """{"message":"503 Service Unavailable"}"""),
            CiAnswer.PipelinesForbidden => gitlab.Answer("GET", PipelinesPath, 403, """{"message":"403 Forbidden"}"""),
            CiAnswer.PipelinesNotFound => gitlab.Answer("GET", PipelinesPath, 404, """{"message":"404 Not Found"}"""),
            CiAnswer.PipelinesConnectionDropped => gitlab.Answer("GET", PipelinesPath, _ => StubReply.DropConnection),
            CiAnswer.FailedPipelineJobsRateLimited => gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson("failed")).Answer("GET", JobsPath, 429, """{"message":"429 Too Many Requests"}"""),
            CiAnswer.PipelinesHiddenFromCredential => NoPipelineListed(gitlab, projectPipelinesStatus: 403),
            CiAnswer.ForkHeadPipelineHidden => NoPipelineListed(gitlab, headPipelineStatus: "failed"),
            CiAnswer.NoPipeline => NoPipelineListed(gitlab),
            CiAnswer.GreenPipeline => gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson("success")).Answer("GET", JobsPath, 200, JobsJson("success", "success")),
            CiAnswer.FailedJob => gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson("failed")).Answer("GET", JobsPath, 200, JobsJson("success", "failed")),
            CiAnswer.FailedPipelineGreenJobs => gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson("failed")).Answer("GET", JobsPath, 200, JobsJson("success", "success")),
            CiAnswer.BlockedPipeline => gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson("manual")).Answer("GET", JobsPath, 200, JobsJson("success", "manual")),
            CiAnswer.DelayedPipeline => gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson("scheduled")).Answer("GET", JobsPath, 200, JobsJson("success", "scheduled")),
            CiAnswer.SkippedPipeline => gitlab.Answer("GET", PipelinesPath, 200, PipelinesJson("skipped")).Answer("GET", JobsPath, 200, JobsJson("skipped")),
            CiAnswer.NewestPipelineFailed => gitlab.Answer("GET", PipelinesPath, 200, $"[{PipelineJson(78, "failed")},{PipelineJson(77, "success")}]").Answer("GET", "/api/v4/projects/4242/pipelines/78/jobs", 200, JobsJson("success", "failed")).Answer("GET", JobsPath, 200, JobsJson("success", "success")),
            _ => throw new ArgumentOutOfRangeException(nameof(answer), answer, null)
        };
    }

    /// <summary>
    /// The merge request lists no pipeline, and GitLab answers what an empty list is checked against: the project's CI
    /// setting, its own pipeline list (403 when the credential may not read pipelines) and the merge request's head
    /// pipeline. The project route answers only its exact path — it is a prefix of every other route here.
    /// </summary>
    private static StubProviderHost NoPipelineListed(StubProviderHost gitlab, int projectPipelinesStatus = 200, string? headPipelineStatus = null) =>
        gitlab.Answer("GET", PipelinesPath, 200, "[]")
            .Answer("GET", ProjectPipelinesPath, projectPipelinesStatus, projectPipelinesStatus == 200 ? "[]" : """{"message":"403 Forbidden"}""")
            .Answer("GET", MergeRequestPath, 200, OpenMergeRequestJson(headPipelineStatus))
            .Answer("GET", ProjectPath, request => request.PathAndQuery == ProjectPath ? new StubReply(200, ProjectJson) : new StubReply(501, """{"message":"no stub configured for this route"}"""));

    private async Task<(Guid RunId, Dictionary<string, WorkflowRunNode> Nodes)> RunGateAsync(string gitlabBaseUrl)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var repositoryId = await SeedGitLabRepositoryAsync(teamId, gitlabBaseUrl);
        var workflowId = await CreateGateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId, payloadJson: JsonSerializer.Serialize(new { repositoryId, number = 7 }));

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        using var verify = _fixture.BeginScope();
        var nodes = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().Where(n => n.RunId == runId).ToDictionaryAsync(n => n.NodeId);

        return (runId, nodes);
    }

    private async Task<Guid> CreateGateWorkflowAsync(Guid teamId, Guid userId)
    {
        const string pullRequest = """{"repositoryId":"{{trigger.repositoryId}}","number":"{{trigger.number}}"}""";

        var definition = new WorkflowDefinition
        {
            SchemaVersion = 1,
            Nodes = new List<NodeDefinition>
            {
                new() { Id = "start",  TypeKey = "trigger.pr.opened",  Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "checks", TypeKey = "git.fetch_pr_checks", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(pullRequest) },
                new() { Id = "gate",   TypeKey = "logic.if",            Config = WorkflowsTestSeed.Json("""{"condition":"{{nodes.checks.outputs.allPassed}} == true"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                new() { Id = "merge",  TypeKey = "git.merge_pr",        Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json(pullRequest) },
                new() { Id = "hold",   TypeKey = "builtin.terminal",    Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() }
            },
            Edges = new List<EdgeDefinition>
            {
                new() { From = "start",  To = "checks" },
                new() { From = "checks", To = "gate" },
                new() { From = "gate",   To = "merge", SourceHandle = "true" },
                new() { From = "gate",   To = "hold",  SourceHandle = "false" }
            }
        };

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand { Name = "ci-gate-" + Guid.NewGuid().ToString("N")[..8], Definition = definition, Activations = new List<WorkflowActivationInput>(), Enabled = true });
    }

    private async Task<Guid> SeedGitLabRepositoryAsync(Guid teamId, string baseUrl)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var payload = scope.Resolve<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = "glpat-loopback" });

        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = "loopback", BaseUrl = baseUrl, ApiUrl = baseUrl };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.Pat, DisplayName = "connection", EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(payload), Status = CredentialStatus.Active };
        var repository = new Repository
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = "4242", NamespacePath = "acme", Name = "api", FullPath = "acme/api",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://gitlab.test/acme/api", Status = RepositoryStatus.Active
        };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);

        await db.SaveChangesAsync();

        return repository.Id;
    }

    private async Task<WorkflowRun> LoadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private static string PipelinesJson(string status) => $"[{PipelineJson(77, status)}]";

    private static string PipelineJson(long id, string status) =>
        $$"""{"id":{{id}},"iid":3,"project_id":4242,"status":"{{status}}","ref":"feature/ci","sha":"0123456789abcdef0123456789abcdef01234567","web_url":"https://gitlab.test/acme/api/-/pipelines/{{id}}"}""";

    private static string OpenMergeRequestJson(string? headPipelineStatus) =>
        $$"""{"id":7007,"iid":7,"project_id":4242,"title":"Gate on CI","state":"opened","source_branch":"feature/ci","target_branch":"main","sha":"0123456789abcdef0123456789abcdef01234567","head_pipeline":{{(headPipelineStatus == null ? "null" : PipelineJson(91, headPipelineStatus))}},"web_url":"https://gitlab.test/acme/api/-/merge_requests/7"}""";

    private const string ProjectJson = """{"id":4242,"name":"api","path":"api","path_with_namespace":"acme/api","default_branch":"main","builds_access_level":"enabled","web_url":"https://gitlab.test/acme/api"}""";

    private static string JobsJson(params string[] statuses) =>
        "[" + string.Join(",", statuses.Select((status, i) => $$"""{"id":{{i + 1}},"name":"job-{{i + 1}}","stage":"test","status":"{{status}}","web_url":"https://gitlab.test/acme/api/-/jobs/{{i + 1}}"}""")) + "]";

    private static readonly string MergedMergeRequestJson = JsonSerializer.Serialize(new
    {
        id = 7007,
        iid = 7,
        project_id = 4242,
        title = "Gate on CI",
        state = "merged",
        merge_commit_sha = "9f8e7d6c5b4a",
        source_branch = "feature/ci",
        target_branch = "main",
        author = new { id = 1, username = "codespace-bot", name = "CodeSpace" },
        created_at = "2026-09-24T08:00:00.000Z",
        updated_at = "2026-09-24T08:00:00.000Z",
        web_url = "https://gitlab.test/acme/api/-/merge_requests/7"
    });
}
