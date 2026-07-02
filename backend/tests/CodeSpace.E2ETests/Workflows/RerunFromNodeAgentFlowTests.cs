using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// P2.2 — agent.code as a from-node rerun ROOT (not just a map-branch body). Re-run a TOP-LEVEL flow from a real
/// <c>agent.code</c> node: the fork mints a new run id, replays the kept upstream agent from the ledger (NO re-stage),
/// and re-stages EXACTLY ONE fresh AgentRun for the from-node target on the forked run id — driving the actual durable
/// agent suspend/resume to completion.
///
/// <para><b>Tier: high-fidelity</b> — the same harness as <see cref="RerunMapBranchAgentFlowTests"/>: the real
/// <see cref="IWorkflowService.RerunFromNodeAsync"/> forks, the real engine re-walks the ReRun closure, the target's
/// <c>agent.code</c> node parks an AgentRun, dispatches the REAL <see cref="Core.Services.Agents.IAgentRunExecutor"/>
/// → real <c>LocalProcessRunner</c> → the <see cref="SubtaskAwareFakeCli"/> process → real ParseEvent/BuildResult →
/// natural resume → the run completes. Only the CLI's intelligence is faked, at the binary (POSIX-only, Rule 12.1).</para>
///
/// <para>This proves the one-line P2.2 disposition flip (<c>RerunDispositions</c> admits <c>ReStageExternalRun</c> as a
/// from-node root) needs NO engine change: a from-node fork's new run id makes the re-staged AgentRun unique by
/// construction, and the stateless agent.code node re-walks through the SAME generic stage chain it uses on a first
/// run. The discriminators mirror the map-branch crown jewel: the fork re-stages EXACTLY ONE fresh AgentRun (distinct
/// Id, the fork's WorkflowRunId, the target's own goal), the kept upstream agent carries zero node.started + is NOT
/// re-staged, and the from-node target ran exactly twice (park-walk + resume-walk).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public class RerunFromNodeAgentFlowTests
{
    private readonly PostgresFixture _fixture;

    public RerunFromNodeAgentFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Rerun_from_an_agent_code_node_restages_only_that_agent_reuses_the_upstream_agent_and_succeeds()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns (Rule 12.1)

        using var cli = new SubtaskAwareFakeCli();

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // an agent.code suspend dispatches the REAL executor + runner + fake CLI, then resumes

        // ── Original: start → agent(a:"Work on alpha") → agent(b:"Work on beta") → end. Both run to Succeeded. ──
        var workflowId = await CreateWorkflowAsync(teamId, userId, TwoAgentChainDef());
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);
        await jobClient.WaitForPendingAsync();

        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);
        var originalAgents = await LoadAgentRunsAsync(originalRunId);
        originalAgents.Count.ShouldBe(2, "the original chain staged one AgentRun per agent node");
        var originalB = originalAgents.Single(r => r.NodeId == "b");
        originalB.IterationKey.ShouldBe("", "a top-level agent.code is keyed TopLevel");

        // ── Rerun FROM node "b". The fork keeps "a" (upstream → replayed, NOT re-staged) and re-stages ONLY "b". ──
        var rerunId = await RerunFromNodeAsync(originalRunId, "b", teamId, userId);
        await RunEngineAsync(rerunId);
        await jobClient.WaitForPendingAsync();

        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        // EXACTLY ONE fresh AgentRun on the fork — the from-node target "b" — keyed (forkRunId, b, TopLevel), distinct
        // Id from the original's "b", carrying its own re-resolved goal. "a" is replayed, never re-staged.
        var forkAgents = await LoadAgentRunsAsync(rerunId);
        forkAgents.Count.ShouldBe(1, "only the from-node target re-stages an agent; the kept upstream agent is replayed, NOT re-staged");
        var forkB = forkAgents[0];
        forkB.NodeId.ShouldBe("b");
        forkB.IterationKey.ShouldBe("", "the re-staged from-node agent is keyed TopLevel");
        forkB.WorkflowRunId.ShouldBe(rerunId, "the re-staged AgentRun belongs to the FORK's run id — the source of its uniqueness");
        forkB.Id.ShouldNotBe(originalB.Id, "a from-node rerun mints a FRESH AgentRun, it never reuses the original's row");
        forkB.Status.ShouldBe(AgentRunStatus.Succeeded, "the re-run target agent executed to completion via the real executor + fake CLI");
        AgentGoalOf(forkB).ShouldBe("Work on beta", "the from-node target re-resolved its own goal");

        // The kept upstream agent "a" carries zero node.started on the fork (replayed from its seeded terminal cell);
        // the target "b" ran EXACTLY twice — one park-walk + one resume-walk (the agent suspend/resume shape).
        (await NodeStartedCountAsync(rerunId, "a")).ShouldBe(0, "the kept upstream agent was replayed, not re-run");
        (await NodeStartedCountAsync(rerunId, "b")).ShouldBe(2, "the from-node target re-ran its agent exactly twice: park-walk + resume-walk");

        // The original run is untouched — its AgentRuns still belong to the original run id (no cross-run mutation).
        (await AgentRunCountAsync(originalRunId)).ShouldBe(2, "the original run's AgentRuns are unchanged by the fork");
    }

    [Fact]
    public async Task Rerun_from_an_agent_with_a_captured_session_carries_the_resume_hint_to_the_restaged_agent()
    {
        if (OperatingSystem.IsWindows()) return;

        // 3.2c CONTINUE keystone, end-to-end through the REAL rerun: the lineage-prior agent captured a resumable
        // session; the producer resolves it and stamps the resume hint onto the FRESH re-staged AgentRun's task, so the
        // re-run agent RESUMES its earlier conversation instead of cold-starting. (The codex fake doesn't capture — only
        // the Claude harness does — so we seed the captured session directly; the producer is harness-agnostic.)
        using var cli = new SubtaskAwareFakeCli();
        var (teamId, userId, originalRunId) = await RunOriginalChainAsync();

        await SeedCapturedSessionAsync(originalRunId, "b", "sess-b-7c3", inlineTranscript: "{\"role\":\"assistant\",\"text\":\"prior beta turn\"}\n", transcriptArtifactId: null);

        var rerunId = await RerunFromNodeAsync(originalRunId, "b", teamId, userId);
        await RunEngineAsync(rerunId);
        await ResolveJobClient().WaitForPendingAsync();
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        var task = await RestagedTaskAsync(rerunId, "b");
        task.ResumeFromSessionId.ShouldBe("sess-b-7c3", "the re-staged agent resumes the lineage-prior run's conversation");
        task.RestoredTranscript.ShouldBe("{\"role\":\"assistant\",\"text\":\"prior beta turn\"}\n", "a small prior transcript rides inline");
        task.RestoredTranscriptArtifactId.ShouldBeNull("a small transcript needs no artifact ref");
    }

    [Fact]
    public async Task Rerun_from_an_agent_whose_prior_captured_no_transcript_cold_starts()
    {
        if (OperatingSystem.IsWindows()) return;

        // Both-or-neither guard: a prior with a session id but NO captured transcript is NOT resumable (a --resume with
        // no transcript would fail "No conversation found"), so the producer stamps nothing and the re-run cold-starts —
        // byte-identical to a fresh run.
        using var cli = new SubtaskAwareFakeCli();
        var (teamId, userId, originalRunId) = await RunOriginalChainAsync();

        await SeedCapturedSessionAsync(originalRunId, "b", "sess-b-notranscript", inlineTranscript: "", transcriptArtifactId: null);

        var rerunId = await RerunFromNodeAsync(originalRunId, "b", teamId, userId);
        await RunEngineAsync(rerunId);
        await ResolveJobClient().WaitForPendingAsync();
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        var task = await RestagedTaskAsync(rerunId, "b");
        task.ResumeFromSessionId.ShouldBeNull("a session id without its transcript is not resumable — the continue cold-starts");
        task.RestoredTranscript.ShouldBeNull();
        task.RestoredTranscriptArtifactId.ShouldBeNull();
    }

    [Fact]
    public async Task Rerun_from_an_agent_with_a_large_offloaded_transcript_carries_the_ref_not_the_bytes()
    {
        if (OperatingSystem.IsWindows()) return;

        // The O(N²) guard: a LARGE prior transcript was offloaded (only its artifact ref lives in result_jsonb), so the
        // producer stamps the REF onto the task — NOT the bytes — keeping task_jsonb bounded (the executor resolves the
        // ref to bytes just-in-time before invocation). Inlining the bytes here would make a continue-chain O(N²).
        using var cli = new SubtaskAwareFakeCli();
        var (teamId, userId, originalRunId) = await RunOriginalChainAsync();

        var artifactId = Guid.NewGuid();
        await SeedCapturedSessionAsync(originalRunId, "b", "sess-b-big", inlineTranscript: "", transcriptArtifactId: artifactId);

        var rerunId = await RerunFromNodeAsync(originalRunId, "b", teamId, userId);
        await RunEngineAsync(rerunId);
        await ResolveJobClient().WaitForPendingAsync();
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);

        var task = await RestagedTaskAsync(rerunId, "b");
        task.ResumeFromSessionId.ShouldBe("sess-b-big");
        task.RestoredTranscriptArtifactId.ShouldBe(artifactId, "a large prior transcript rides as a REF, keeping task_jsonb bounded");
        task.RestoredTranscript.ShouldBeNull("the bytes are NOT inlined into task_jsonb (the O(N²) guard)");
    }

    [Fact]
    public async Task A_sibling_rerun_resumes_its_ancestors_session_not_the_other_forks()
    {
        if (OperatingSystem.IsWindows()) return;

        // Anti-contamination (review Finding 1): re-running the SAME original twice makes two SIBLING forks. The second
        // fork descends from the original A — NOT from the first fork B — so it must resume A's conversation, never B's.
        // The old flat-lineage selection ("most-recent at the cell across the whole root group") would wrongly pick B.
        using var cli = new SubtaskAwareFakeCli();
        var (teamId, userId, originalRunId) = await RunOriginalChainAsync();

        await SeedCapturedSessionAsync(originalRunId, "b", "sess-A", inlineTranscript: "A-transcript\n", transcriptArtifactId: null);

        var forkB = await RerunFromNodeAsync(originalRunId, "b", teamId, userId);
        await RunEngineAsync(forkB);
        await ResolveJobClient().WaitForPendingAsync();
        await SeedCapturedSessionAsync(forkB, "b", "sess-B", inlineTranscript: "B-transcript\n", transcriptArtifactId: null);

        var forkC = await RerunFromNodeAsync(originalRunId, "b", teamId, userId);   // a SIBLING of B — ParentRunId == A
        forkC.ShouldNotBe(forkB, "re-running the same original twice mints two distinct sibling forks");
        await RunEngineAsync(forkC);
        await ResolveJobClient().WaitForPendingAsync();
        await AssertRunStatusAsync(forkC, WorkflowRunStatus.Success);

        var task = await RestagedTaskAsync(forkC, "b");
        task.ResumeFromSessionId.ShouldBe("sess-A", "a sibling fork resumes its ANCESTOR (A), never the other branch (B)");
        task.RestoredTranscript.ShouldBe("A-transcript\n", "and it restores A's transcript, not B's");
    }

    [Fact]
    public async Task Rerun_from_an_agent_with_a_corrupt_prior_result_cold_starts_without_failing()
    {
        if (OperatingSystem.IsWindows()) return;

        // Robustness (review Finding, security lens): a prior with a session id but an UNDESERIALIZABLE result_jsonb must
        // degrade to cold-start — the contract — NOT throw and fail the whole re-run. result_jsonb is a Postgres jsonb
        // column (syntactically-invalid JSON can't be stored), so the reachable corruption is VALID json with a wrong
        // type — here a non-Guid string for the artifact-id field, which throws JsonException on deserialize.
        using var cli = new SubtaskAwareFakeCli();
        var (teamId, userId, originalRunId) = await RunOriginalChainAsync();

        await SeedRawResultAsync(originalRunId, "b", "sess-corrupt", "{\"sessionTranscriptArtifactId\":\"not-a-guid\"}");

        var rerunId = await RerunFromNodeAsync(originalRunId, "b", teamId, userId);
        await RunEngineAsync(rerunId);
        await ResolveJobClient().WaitForPendingAsync();
        await AssertRunStatusAsync(rerunId, WorkflowRunStatus.Success);   // NOT Failure — the corrupt prior is skipped, the re-run still runs

        var task = await RestagedTaskAsync(rerunId, "b");
        task.ResumeFromSessionId.ShouldBeNull("a corrupt prior result is not resumable — cold-start, never a hard failure");
    }

    [Fact]
    public async Task Find_resumable_session_is_team_scoped_and_never_crosses_teams()
    {
        if (OperatingSystem.IsWindows()) return;

        // Tenancy (review Finding, security lens): a run can NEVER resume another team's captured session. Same-team
        // resolves it (proving the cross-team null is a tenancy gate, not a dead lookup).
        using var cli = new SubtaskAwareFakeCli();
        var (teamId, _, originalRunId) = await RunOriginalChainAsync();
        await SeedCapturedSessionAsync(originalRunId, "b", "sess-team-a", inlineTranscript: "A-only\n", transcriptArtifactId: null);

        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<Core.Services.Agents.IAgentRunService>();

        (await svc.FindResumableSessionAsync(otherTeamId, originalRunId, "b", "", CancellationToken.None))
            .ShouldBeNull("a run can never resume another team's captured session");

        (await svc.FindResumableSessionAsync(teamId, originalRunId, "b", "", CancellationToken.None))!.SessionId
            .ShouldBe("sess-team-a", "the same-team lookup DOES resolve the ancestor's session — the null above is tenancy, not a dead query");
    }

    // ── Definition: start → agent(a) → agent(b) → end. Two top-level real agent.code nodes with distinct goals. ──
    private static WorkflowDefinition TwoAgentChainDef() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "a", TypeKey = "agent.code",
                    Config = WorkflowsTestSeed.Json("""{ "goal": "Work on alpha", "harness": "codex-cli" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "b", TypeKey = "agent.code",
                    Config = WorkflowsTestSeed.Json("""{ "goal": "Work on beta", "harness": "codex-cli" }"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "a" },
            new() { From = "a", To = "b" },
            new() { From = "b", To = "end" },
        },
    };

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "fromnode-agent-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> RerunFromNodeAsync(Guid originalRunId, string fromNodeId, Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IWorkflowService>().RerunFromNodeAsync(originalRunId, fromNodeId, teamId, userId, CancellationToken.None);
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private InMemoryBackgroundJobClient ResolveJobClient()
    {
        using var scope = _fixture.BeginScope();
        return scope.Resolve<InMemoryBackgroundJobClient>();
    }

    private async Task AssertRunStatusAsync(Guid runId, WorkflowRunStatus expected)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(expected, $"run {runId}; error={run.Error}");
    }

    private async Task<int> AgentRunCountAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().CountAsync(r => r.WorkflowRunId == runId);
    }

    private async Task<List<Core.Persistence.Entities.AgentRun>> LoadAgentRunsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().Where(r => r.WorkflowRunId == runId).ToListAsync();
    }

    private static string AgentGoalOf(Core.Persistence.Entities.AgentRun run) =>
        JsonSerializer.Deserialize<Messages.Agents.AgentTask>(run.TaskJson, Core.Services.Agents.AgentJson.Options)!.Goal;

    // Run the original start→a→b→end chain to Success (the caller holds the fake CLI), returning the ids the rerun needs.
    private async Task<(Guid TeamId, Guid UserId, Guid OriginalRunId)> RunOriginalChainAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;

        var workflowId = await CreateWorkflowAsync(teamId, userId, TwoAgentChainDef());
        var originalRunId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(originalRunId);
        await jobClient.WaitForPendingAsync();
        await AssertRunStatusAsync(originalRunId, WorkflowRunStatus.Success);

        return (teamId, userId, originalRunId);
    }

    // Simulate a prior agent having CAPTURED a resumable session: set the session-id column + fold the transcript
    // (inline OR an artifact ref) into result_jsonb — the exact state the CONTINUE producer reads from the lineage.
    private async Task SeedCapturedSessionAsync(Guid runId, string nodeId, string sessionId, string inlineTranscript, Guid? transcriptArtifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.AgentRun.SingleAsync(r => r.WorkflowRunId == runId && r.NodeId == nodeId);
        run.SessionId = sessionId;

        var result = JsonSerializer.Deserialize<Messages.Agents.AgentRunResult>(run.ResultJson!, Core.Services.Agents.AgentJson.Options)!;
        run.ResultJson = JsonSerializer.Serialize(result with { SessionTranscript = inlineTranscript, SessionTranscriptArtifactId = transcriptArtifactId }, Core.Services.Agents.AgentJson.Options);

        await db.SaveChangesAsync();
    }

    // Seed a session-id column + a RAW (possibly malformed) result_jsonb — to prove a corrupt prior degrades to cold-start.
    private async Task SeedRawResultAsync(Guid runId, string nodeId, string sessionId, string rawResultJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.AgentRun.SingleAsync(r => r.WorkflowRunId == runId && r.NodeId == nodeId);
        run.SessionId = sessionId;
        run.ResultJson = rawResultJson;

        await db.SaveChangesAsync();
    }

    private async Task<Messages.Agents.AgentTask> RestagedTaskAsync(Guid rerunId, string nodeId)
    {
        var runs = await LoadAgentRunsAsync(rerunId);
        return JsonSerializer.Deserialize<Messages.Agents.AgentTask>(runs.Single(r => r.NodeId == nodeId).TaskJson, Core.Services.Agents.AgentJson.Options)!;
    }

    private async Task<int> NodeStartedCountAsync(Guid runId, string nodeId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunRecord.AsNoTracking()
            .CountAsync(r => r.RunId == runId && r.NodeId == nodeId && r.IterationKey == "" && r.RecordType == WorkflowRunRecordTypes.NodeStarted);
    }
}
