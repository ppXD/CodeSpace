using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 SOTA #2 (supervisor-sees-its-agents) — the rehydrate-fold proof over REAL Postgres + the REAL
/// <see cref="SupervisorTurnService"/>.<c>RehydrateFromDecisionLogAsync</c> (the same method the turn loop calls).
/// Seeds a terminal <c>spawn</c> decision + its spawned <see cref="AgentRun"/> rows directly (one Succeeded with a
/// result, one Cancelled with a ROW error + NULL result — the abandoned-agent shape the slice most needs to
/// surface), then asserts the rehydrate folds each agent's COMPACT result into the spawn decision's outcome so the
/// decider can perceive it.
///
/// <para>Crown jewels: a Failed/Cancelled agent whose ResultJson is null still surfaces its ROW error; the fold is
/// ADDITIVE (agentCount byte-intact → the E5 spawn-cap counter unperturbed); it is PERSISTED once + idempotent on
/// re-rehydrate; a NON-terminal spawn row is NEVER folded; and a real decider prompt built from the rehydrated
/// context contains each agent's status + summary + error.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorAgentResultsRehydrateFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorAgentResultsRehydrateFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task Rehydrate_folds_each_spawned_agents_compact_result_into_the_terminal_spawn_outcome()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var okId = Guid.NewGuid();
        var cancelledId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, okId, AgentRunStatus.Succeeded, rowError: null,
            resultJson: ResultJson(summary: "added the endpoint", changedFiles: new[] { "Api/Foo.cs" }, producedBranch: "codespace/agent/ok"));
        // The abandoned-agent shape: a ROW error, NO ResultJson — the signal the decider most needs.
        await SeedAgentRunAsync(runId, teamId, cancelledId, AgentRunStatus.Cancelled, rowError: "lease expired mid-run", resultJson: null);

        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, SpawnOutcome(okId, cancelledId));

        var context = await RehydrateAsync(runId, teamId);

        var spawn = context.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var results = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson);
        results.Count.ShouldBe(2, "both spawned agents are folded into the spawn outcome");

        var ok = results.Single(r => r.AgentRunId == okId);
        ok.Status.ShouldBe("Succeeded");
        ok.Summary.ShouldBe("added the endpoint");
        ok.ChangedFiles.ShouldBe(new[] { "Api/Foo.cs" });

        var cancelled = results.Single(r => r.AgentRunId == cancelledId);
        cancelled.Status.ShouldBe("Cancelled");
        cancelled.Error.ShouldBe("lease expired mid-run", "an abandoned agent with NULL ResultJson still surfaces its ROW error");

        // ADDITIVE: agentCount stays byte-intact so the E5 spawn-cap / no-progress counters are unperturbed.
        SupervisorOutcome.ReadStagedAgentCount(spawn.OutcomeJson).ShouldBe(2);
        SupervisorOutcome.ReadStagedAgentRunIds(spawn.OutcomeJson).ShouldBe(new[] { okId, cancelledId });

        // The real decider PROMPT built from the rehydrated context surfaces the work products + the failure.
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(context);
        prompt.ShouldContain("added the endpoint", Case.Insensitive);
        prompt.ShouldContain("lease expired mid-run", Case.Insensitive, "the decider sees the abandoned agent's failure — the retry signal");
    }

    [Fact]
    public async Task Rehydrate_folds_a_multi_repo_agents_per_repo_results_into_the_compact()
    {
        // Resolver loop #379 S7-B — a MULTI-repo agent's per-repo outcomes (result_jsonb RepositoryResults) survive the
        // rehydrate fold into the durable compact agentResults, so the resolver loop reads each repo's pushed branch +
        // identity straight off the ledger (S7-D). A SINGLE-repo agent in the same spawn folds EMPTY per-repo results —
        // the 1-repo case, behaviour-identical.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var multiId = Guid.NewGuid();
        var singleId = Guid.NewGuid();
        var apiRepo = Guid.NewGuid();
        var webRepo = Guid.NewGuid();

        await SeedAgentRunAsync(runId, teamId, multiId, AgentRunStatus.Succeeded, rowError: null, resultJson: ResultJson(
            summary: "coordinated change", producedBranch: "codespace/agent/api", repositoryResults: new[]
            {
                new RepositoryRunResult { Alias = "repo", RepositoryId = apiRepo, ChangedFiles = new[] { "Api/Foo.cs" }, ProducedBranch = "codespace/agent/api", BaseSha = "a1b2c3d4", BaseBranch = "main", Access = WorkspaceAccess.Write },
                new RepositoryRunResult { Alias = "web", RepositoryId = webRepo, ChangedFiles = new[] { "web/Bar.tsx" }, ProducedBranch = "codespace/agent/web", BaseBranch = "develop", Access = WorkspaceAccess.Write },
            }));
        await SeedAgentRunAsync(runId, teamId, singleId, AgentRunStatus.Succeeded, rowError: null,
            resultJson: ResultJson(summary: "single repo", producedBranch: "codespace/agent/solo"));

        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, SpawnOutcome(multiId, singleId));

        var context = await RehydrateAsync(runId, teamId);

        var spawn = context.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var results = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson);

        var multi = results.Single(r => r.AgentRunId == multiId);
        multi.RepositoryResults.Count.ShouldBe(2, "the multi-repo agent's per-repo outcomes are folded into the durable compact");
        multi.RepositoryResults.Select(r => r.ProducedBranch).ShouldBe(new[] { "codespace/agent/api", "codespace/agent/web" });
        multi.RepositoryResults.Single(r => r.Alias == "web").RepositoryId.ShouldBe(webRepo, "per-repo identity survives the durable ledger — the per-repo resolution/PR-open key");
        multi.RepositoryResults.Single(r => r.Alias == "repo").BaseBranch.ShouldBe("main");
        multi.RepositoryResults.Single(r => r.Alias == "repo").BaseSha.ShouldBe("a1b2c3d4", "the per-repo integrity anchor (SOTA #3 stale-base refusal SHA) survives real persistence — S7-C/D's per-repo integrate consumes it");

        var single = results.Single(r => r.AgentRunId == singleId);
        single.RepositoryResults.ShouldBeEmpty("a single-repo agent folds no per-repo entries — the 1-repo case, behaviour-identical");
        single.ProducedBranch.ShouldBe("codespace/agent/solo", "its one outcome stays on the top-level field");
    }

    [Fact]
    public async Task Rehydrate_persists_the_fold_once_and_is_idempotent_on_re_rehydrate()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, AgentRunStatus.Succeeded, rowError: null, resultJson: ResultJson(summary: "done"));
        var bareOutcome = SpawnOutcome(agentId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, bareOutcome);

        await RehydrateAsync(runId, teamId);

        // The fold is PERSISTED onto the durable ledger row (survives restart without re-resolving).
        var afterFirst = await LedgerOutcomeAsync(runId, teamId, SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(afterFirst).Count.ShouldBe(1, "the agent result was folded + persisted onto the ledger row");
        SupervisorOutcome.ReadStagedAgentCount(afterFirst).ShouldBe(1, "agentCount stays intact through the persisted fold");

        // Re-rehydrate → the orchestrator SKIPS the already-folded row (returns it verbatim), so the in-memory
        // outcome equals the durable read-back and NO redundant write is issued (the skip the review asked for).
        var ctx2 = await RehydrateAsync(runId, teamId);
        var spawn2 = ctx2.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        spawn2.OutcomeJson.ShouldBe(afterFirst, "2nd rehydrate returns the already-folded row verbatim — no re-fold, no redundant UPDATE");
        (await LedgerOutcomeAsync(runId, teamId, SupervisorDecisionKinds.Spawn)).ShouldBe(afterFirst, "the durable row is unchanged across the second rehydrate");
    }

    [Fact]
    public async Task Rehydrate_does_NOT_fold_a_non_terminal_spawn_row()
    {
        // A Running spawn row carrying agentRunIds (the re-park shape) must NOT be rewritten here — it would be
        // clobbered by the later RecordTerminalAsync. The fold is scoped to TERMINAL rows only.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var agentId = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, agentId, AgentRunStatus.Succeeded, rowError: null, resultJson: ResultJson(summary: "done"));
        var bareOutcome = SpawnOutcome(agentId);
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Running, bareOutcome);

        var context = await RehydrateAsync(runId, teamId);

        context.InFlight.ShouldNotBeNull("a Running spawn is the in-flight decision");
        SupervisorOutcome.ReadAgentResults(context.InFlight!.OutcomeJson).ShouldBeEmpty("a non-terminal spawn is NOT folded in-memory");
        // The durable row was never rewritten with an agent-results fold (it would be clobbered by RecordTerminalAsync).
        SupervisorOutcome.ReadAgentResults(await LedgerOutcomeAsync(runId, teamId, SupervisorDecisionKinds.Spawn))
            .ShouldBeEmpty("the non-terminal ledger row carries no folded agentResults");
    }

    [Fact]
    public async Task Rehydrate_folds_an_Unknown_placeholder_for_a_staged_id_that_no_longer_resolves()
    {
        // Forward-looking robustness: a staged agent-run id with NO resolvable AgentRun row (deleted / out-of-team —
        // not reachable today) folds an explicit Unknown placeholder, so the folded set is N-for-N and the decider
        // never sees a silent hole shorter than agentCount.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var okId = Guid.NewGuid();
        var ghostId = Guid.NewGuid();   // never seeded — no AgentRun row exists for it
        await SeedAgentRunAsync(runId, teamId, okId, AgentRunStatus.Succeeded, rowError: null, resultJson: ResultJson(summary: "done"));
        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, SpawnOutcome(okId, ghostId));

        var context = await RehydrateAsync(runId, teamId);
        var spawn = context.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var results = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson);

        results.Count.ShouldBe(2, "the folded set is N-for-N even when an id does not resolve");
        results.Single(r => r.AgentRunId == okId).Status.ShouldBe("Succeeded");
        var ghost = results.Single(r => r.AgentRunId == ghostId);
        ghost.Status.ShouldBe("Unknown", "an unresolved staged id folds an explicit Unknown placeholder, not a silent omission");
        ghost.Error.ShouldNotBeNull();
        SupervisorOutcome.ReadStagedAgentCount(spawn.OutcomeJson).ShouldBe(2, "agentCount stays intact");
    }

    // ── Evidence-based no-progress (W3) — the streak over a REAL durable tape ─────────

    [Fact]
    public async Task Rehydrate_counts_an_all_failed_spawn_wave_as_no_progress()
    {
        // THE BUG FIX, end-to-end over real Postgres: a spawn whose agents ALL failed (no diff, no branch) produced
        // NO settled evidence, so the no-progress streak must INCREMENT. The prior staged-COUNT heuristic counted it
        // as progress (streak 0), letting a loop spawning never-succeeding agents never trip the stall bound.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var failedA = Guid.NewGuid();
        var failedB = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, failedA, AgentRunStatus.Failed, rowError: "build failed: CS1002", resultJson: null);
        await SeedAgentRunAsync(runId, teamId, failedB, AgentRunStatus.TimedOut, rowError: "idle timeout", resultJson: null);

        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, SpawnOutcome(failedA, failedB));

        var context = await RehydrateAsync(runId, teamId);

        context.NoProgressDecisions.ShouldBe(1, "an all-failed, no-artifact spawn wave is NOT progress — the streak increments");
    }

    [Fact]
    public async Task Rehydrate_resets_no_progress_on_a_spawn_that_produced_evidence()
    {
        // The non-breaking other half: a spawn whose agent produced a real artifact (a git diff + branch) resets the
        // streak to 0 — exactly as before. A genuinely-progressing run is NEVER falsely stalled by the stricter rule.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var ok = Guid.NewGuid();
        await SeedAgentRunAsync(runId, teamId, ok, AgentRunStatus.Succeeded, rowError: null,
            resultJson: ResultJson(summary: "added the endpoint", changedFiles: new[] { "Api/Foo.cs" }, producedBranch: "codespace/agent/ok"));

        await SeedSpawnDecisionAsync(runId, teamId, sequence: 1, SupervisorDecisionStatus.Succeeded, SpawnOutcome(ok));

        var context = await RehydrateAsync(runId, teamId);

        context.NoProgressDecisions.ShouldBe(0, "a spawn that produced a real artifact reset the streak — no false stall");
    }

    // ── P1e compaction ladder over the REAL durable tape ────────────────────────────────

    [Fact]
    public async Task Rehydrate_then_render_compacts_a_superseded_plans_full_payload()
    {
        // P1e end-to-end over real Postgres: two plan versions on the durable tape. After the REAL rehydrate, the real
        // decider prompt renders ONLY the latest plan full; the superseded v1's full subtask payload is dropped for a
        // one-line digest — the monotone-prompt-growth fix proven through the actual DB → rehydrate → BuildUserPrompt
        // path (not a hand-built context), including that a genuinely persisted, re-loaded plan compacts correctly.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanDecisionAsync(runId, teamId, sequence: 1, """{"goal":"g","subtasks":[{"id":"a","title":"A","instruction":"OLD_PLAN_INSTRUCTION_V1"},{"id":"b","title":"B","instruction":"bee"}]}""");
        await SeedPlanDecisionAsync(runId, teamId, sequence: 2, """{"goal":"g","subtasks":[{"id":"a","title":"A","instruction":"NEW_PLAN_INSTRUCTION_V2"},{"id":"c","title":"C","instruction":"see"}]}""");

        var context = await RehydrateAsync(runId, teamId);
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(context);

        prompt.ShouldNotContain("OLD_PLAN_INSTRUCTION_V1", Case.Insensitive, "the superseded plan's full subtask payload is dropped from the rehydrated prompt");
        prompt.ShouldContain("plan (superseded by a later re-plan): 2 subtask(s) [a, b]", Case.Insensitive, "the superseded plan collapses to a one-line digest keeping its subtask ids");
        prompt.ShouldContain("NEW_PLAN_INSTRUCTION_V2", Case.Insensitive, "the latest plan still renders full — only superseded plans compact");
    }

    // ─── Seeding + helpers ─────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        // A real WorkflowRun id satisfies the FKs the ledger + agent rows reference; the rehydrate reads the tape,
        // not the run shape, so a bare manual run is sufficient anchoring.
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-rehydrate-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task SeedAgentRunAsync(Guid runId, Guid teamId, Guid agentRunId, AgentRunStatus status, string? rowError, string? resultJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId,
            TeamId = teamId,
            WorkflowRunId = runId,
            NodeId = NodeId,
            IterationKey = $"{NodeId}#turn0",
            Harness = "codex-cli",
            Status = status,
            Error = rowError,
            TaskJson = "{}",
            ResultJson = resultJson,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedSpawnDecisionAsync(Guid runId, Guid teamId, long sequence, SupervisorDecisionStatus status, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            Sequence = sequence,
            DecisionKind = SupervisorDecisionKinds.Spawn,
            IdempotencyKey = $"spawn-{sequence}-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = status,
            PayloadJson = """{"subtaskIds":["s1","s2"]}""",
            OutcomeJson = outcomeJson,
            FenceEpoch = 1,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedPlanDecisionAsync(Guid runId, Guid teamId, long sequence, string payloadJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            Sequence = sequence,
            DecisionKind = SupervisorDecisionKinds.Plan,
            IdempotencyKey = $"plan-{sequence}-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = payloadJson,
            OutcomeJson = """{"outcome":"planned"}""",
            FenceEpoch = 1,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<SupervisorTurnContext> RehydrateAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<ISupervisorTurnService>().RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig: null, CancellationToken.None);
    }

    private async Task<string?> LedgerOutcomeAsync(Guid runId, Guid teamId, string kind)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == kind)
            .Select(d => d.OutcomeJson)
            .SingleAsync();
    }

    private static string SpawnOutcome(params Guid[] agentRunIds) =>
        JsonSerializer.Serialize(new { agentRunIds, agentCount = agentRunIds.Length }, AgentJson.Options);

    private static string ResultJson(string? summary = null, string[]? changedFiles = null, string? producedBranch = null, RepositoryRunResult[]? repositoryResults = null) =>
        JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded,
            ExitReason = "completed",
            Summary = summary,
            ChangedFiles = changedFiles ?? Array.Empty<string>(),
            ProducedBranch = producedBranch,
            RepositoryResults = repositoryResults ?? Array.Empty<RepositoryRunResult>(),
        }, AgentJson.Options);
}
