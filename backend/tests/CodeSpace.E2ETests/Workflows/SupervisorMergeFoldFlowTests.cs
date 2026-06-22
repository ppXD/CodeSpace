using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure.Jobs;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// 🟢 THE S1 merge-fidelity crown jewel (HIGH fidelity — same real spine as
/// <see cref="SupervisorRealAgentE2ETests"/>: real engine + real <see cref="SupervisorTurnService"/> +
/// <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> + real
/// <see cref="Core.Services.Agents.AgentRunService"/> + real <see cref="Core.Services.Agents.AgentRunExecutor"/>
/// + real <c>LocalProcessRunner</c> SPAWNING A REAL OS PROCESS over real Postgres; the scripted decider stands
/// in for the LLM, the CLI's intelligence is faked at the binary).
///
/// <para>It drives the plan → spawn → MERGE → stop arc (<see cref="SupervisorDecisionScript.PlanSpawnMergeStop"/>):
/// two real agents execute through the production pipeline, the barrier resumes, and the supervisor's MERGE turn
/// folds their results. The assertion under test is the S1 fix: the merge decision's recorded outcome carries
/// each agent's FULL <see cref="Messages.Agents.AgentRunResult"/> contribution — <c>changedFiles</c>,
/// <c>producedBranch</c>, <c>patch</c>, <c>error</c> AND <c>summary</c> — NOT just the summary the old
/// <c>ReadSummary</c> narrowing kept (which discarded the real work products). A regression to summary-only
/// fails here because the merged entries would no longer carry the work-product keys.</para>
///
/// <para>POSIX-only: the fake CLI is a <c>/bin/sh</c> script the runner spawns. Skipped on Windows (Rule 12.1).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class SupervisorMergeFoldFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string? _flagBefore;

    public SupervisorMergeFoldFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _flagBefore = Environment.GetEnvironmentVariable(SupervisorLane.EnabledEnvVar);
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(SupervisorLane.EnabledEnvVar, _flagBefore);

        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanThenStop();
        scope.Resolve<InMemoryBackgroundJobClient>().AutoExecute = true;
    }

    [Fact]
    public async Task Merge_folds_the_full_agent_contribution_not_just_the_summary()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new SubtaskAwareFakeCli();

        SetDecisionScriptToPlanSpawnMergeStop();

        var jobClient = ResolveJobClient();
        jobClient.Clear();
        jobClient.AutoExecute = true;   // the spawn dispatch runs the REAL executor + runner + fake CLI; merge + stop self-advance

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        // One drain walks the whole plan → spawn(2 real agents) → barrier → merge → stop chain to Success.
        await RunEngineAsync(runId);
        await jobClient.WaitForPendingAsync();

        await AssertRunReachedSuccessAsync(runId);
        await AssertDecisionLedgerIsPlanSpawnMergeStopAsync(runId, teamId);
        await AssertMergeFoldsFullContributionAsync(runId, teamId);
    }

    // ─── Assertions ──────────────────────────────────────────────────────────────────

    private async Task AssertRunReachedSuccessAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: "the plan→spawn→merge→stop arc must reach Success; if not, inspect the AgentRun.Error + the failed WorkflowRunNode rows for this run");
    }

    private async Task AssertDecisionLedgerIsPlanSpawnMergeStopAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        IReadOnlyList<string> kinds = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId)
            .OrderBy(d => d.Sequence)
            .Select(d => d.DecisionKind)
            .ToListAsync();

        kinds.ShouldBe(
            new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop },
            customMessage: "the decision ledger must record plan/spawn/merge/stop in Sequence order — proving the merge turn ran after both real agents completed through the barrier");
    }

    /// <summary>The S1 assertion: the merge decision's recorded outcome carries each agent's FULL contribution — every merged entry exposes the work-product keys (changedFiles / producedBranch / patch / error), not just summary — and the summaries are the real fake-CLI-derived folds, proving the merge read the real ResultJson off disk and DID NOT narrow to summary-only.</summary>
    private async Task AssertMergeFoldsFullContributionAsync(Guid runId, Guid teamId)
    {
        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var merge = await db.SupervisorDecisionRecord.AsNoTracking()
            .SingleAsync(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Merge);

        merge.OutcomeJson.ShouldNotBeNull("the merge decision must record a folded outcome");

        var outcome = JsonDocument.Parse(merge.OutcomeJson!).RootElement;
        outcome.GetProperty("count").GetInt32().ShouldBe(2, "the merge folded both prior-spawn agent results");
        outcome.GetProperty("synthesisInstruction").GetString().ShouldBe("combine both branches", "the merge carries the decider's synthesis instruction");

        var merged = outcome.GetProperty("merged").EnumerateArray().ToList();
        merged.Count.ShouldBe(2, "one merged entry per spawned agent");

        foreach (var entry in merged)
        {
            // The S1 fix: the FULL work products are present per entry — a summary-only fold would lack these.
            entry.TryGetProperty("changedFiles", out var changed).ShouldBeTrue("each merged entry must carry the agent's changedFiles — the work product the old summary-only fold discarded");
            changed.ValueKind.ShouldBe(JsonValueKind.Array, "changedFiles is folded as an array (empty for a no-workspace run, but PRESENT)");
            entry.TryGetProperty("patch", out _).ShouldBeTrue("each merged entry must carry the agent's patch — the diff a downstream PR-open step consumes");
            entry.TryGetProperty("producedBranch", out _).ShouldBeTrue("each merged entry must carry the agent's producedBranch — the branch handoff");
            entry.TryGetProperty("error", out _).ShouldBeTrue("each merged entry must carry the agent's error slot");
            entry.GetProperty("status").GetString().ShouldBe(nameof(AgentRunStatus.Succeeded));
        }

        var summaries = merged.Select(e => e.GetProperty("summary").GetString()).OrderBy(s => s).ToList();
        var expected = new[] { SubtaskAwareFakeCli.ExpectedSummaryFor("do alpha"), SubtaskAwareFakeCli.ExpectedSummaryFor("do beta") }.OrderBy(s => s).ToList();

        summaries.ShouldBe(expected,
            customMessage: "the folded summaries must be the real fake-CLI transform of the planned subtask instructions — proving the merge read each agent's real persisted ResultJson, not a fabricated stand-in");
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────────

    private void SetDecisionScriptToPlanSpawnMergeStop()
    {
        using var scope = _fixture.BeginScope();
        scope.Resolve<SupervisorDecisionScript>().PlanSpawnMergeStop();
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "sup-merge-fold-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = SupervisorDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
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

    // manual → sup (agent.supervisor) → terminal (the SAME shape SupervisorRealAgentE2ETests builds).
    private static WorkflowDefinition SupervisorDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "sup", TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "sup" },
            new() { From = "sup", To = "end" },
        },
    };
}
