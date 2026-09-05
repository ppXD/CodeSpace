using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Workflows.Artifacts.Exceptions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

using CodeSpace.Tests.Fakes;
namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/> + real
/// <see cref="Core.Services.Supervisor.Executors.RealSupervisorActionExecutor"/> merge): loopability slice 4
/// ("局部綠≠整合綠") end-to-end — a merge withholds a per-unit-REJECTED unit's branch. A prior spawn produced two
/// units, one of which FAILED its per-unit acceptance (slice 3); the merge turn folds ONLY the accepted unit's
/// contribution, and the rejected unit's branch never reaches the reviewable head. The withhold filter itself is
/// pinned in isolation by <c>SupervisorMergeWithholdTests</c>; this proves the wiring through the real executor +
/// the real AgentRun load over real Postgres.
///
/// <para>The same door's other half is here too: which contributors a merge folds when a RE-PLAN moved the
/// generation boundary past the wave that produced them (<see cref="SupervisorMergeContributors"/>). The selection
/// is pinned in isolation by <c>SupervisorMergeCarryOverTests</c>; these prove it through the real executor.</para>
///
/// <para>And the ONE thing that overrides that conservation: a plan whose payload declares
/// <c>abandonEarlierResults</c> — the model saying the earlier generation was the wrong DIRECTION. The merge that
/// follows folds none of it, and the plan's own ledger row records how much it discarded.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorMergeWithholdFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorMergeWithholdFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";

    [Fact]
    public async Task A_merge_folds_only_the_accepted_unit_and_withholds_the_rejected_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var acceptedId = Guid.NewGuid();
        var rejectedId = Guid.NewGuid();

        // A prior spawn whose folded agentResults carry the per-unit verdicts: accepted unit PASSED, rejected unit FAILED.
        await SeedSpawnAsync(runId, teamId, sequence: 1,
            Unit(acceptedId, "codespace/agent/accepted", acceptancePassed: true),
            Unit(rejectedId, "codespace/agent/rejected", acceptancePassed: false));

        // Both agents' real terminal rows exist (so the load would find EITHER) — proving the filter, not a missing row,
        // is what withholds the rejected one.
        await SeedAgentRunAsync(acceptedId, teamId, runId, "codespace/agent/accepted");
        await SeedAgentRunAsync(rejectedId, teamId, runId, "codespace/agent/rejected");

        var merge = await RunMergeTurnAsync(runId, teamId);

        var outcome = JsonDocument.Parse(merge!).RootElement;
        outcome.GetProperty("count").GetInt32().ShouldBe(1, "only the accepted unit is folded — the rejected one is withheld");

        var branches = outcome.GetProperty("merged").EnumerateArray()
            .Select(e => e.GetProperty("producedBranch").GetString()).ToList();
        branches.Count.ShouldBe(1, "the merged head carries ONLY the accepted unit — the rejected one never reaches it");
        branches[0].ShouldBe("codespace/agent/accepted", "the surviving branch is the accepted unit's");
    }

    [Fact]
    public async Task A_merge_of_an_all_ungraded_wave_folds_every_unit_byte_identical_to_pre_slice()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 1, Unit(a, "codespace/agent/a", null), Unit(b, "codespace/agent/b", null));
        await SeedAgentRunAsync(a, teamId, runId, "codespace/agent/a");
        await SeedAgentRunAsync(b, teamId, runId, "codespace/agent/b");

        var merge = await RunMergeTurnAsync(runId, teamId);

        var outcome = JsonDocument.Parse(merge!).RootElement;
        outcome.GetProperty("count").GetInt32().ShouldBe(2, "no per-unit verdicts → every unit folds, exactly as before the slice");
        outcome.TryGetProperty("contributorIntegrity", out _).ShouldBeFalse("a healthy merge keeps the pre-integrity outcome byte shape");
        outcome.TryGetProperty("carriedOverFromEarlierGenerations", out _).ShouldBeFalse("a merge whose own generation staged the work records no carry-over");
    }

    [Fact]
    public async Task A_replan_after_the_wave_finished_still_merges_the_results_it_stranded()
    {
        // The live trajectory: plan(2) → spawn×2 (both Succeeded and pushed) → plan(1). The plan window slices the
        // tape at the newest plan, which has no spawn after it, so a window-scoped merge folded ZERO — two finished,
        // pushed, accepted results became unmergeable and the publish that followed resolved no target at all.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, "s1", "s2");
        await SeedSpawnAsync(runId, teamId, sequence: 2, Unit(a, "codespace/agent/a", acceptancePassed: true), Unit(b, "codespace/agent/b", acceptancePassed: true));
        await SeedAgentRunAsync(a, teamId, runId, "codespace/agent/a");
        await SeedAgentRunAsync(b, teamId, runId, "codespace/agent/b");
        await SeedPlanAsync(runId, teamId, sequence: 3, "s3");

        var outcome = JsonDocument.Parse((await RunMergeTurnAsync(runId, teamId))!).RootElement;

        outcome.GetProperty("count").GetInt32().ShouldBe(2, "a plan-generation boundary may supersede an instruction — it must not make FINISHED work invisible to the merge");
        outcome.GetProperty("merged").EnumerateArray().Select(e => e.GetProperty("producedBranch").GetString())
            .ShouldBe(new[] { "codespace/agent/a", "codespace/agent/b" }, "both stranded branches reach the reviewable head, in the order they were produced");
        outcome.GetProperty("carriedOverFromEarlierGenerations").GetInt32().ShouldBe(2, "the outcome states plainly that this merge conserved work an earlier generation produced");
        outcome.TryGetProperty("contributorIntegrity", out _).ShouldBeFalse("the carried-over contributors materialized faithfully — nothing to report");
    }

    [Fact]
    public async Task A_replan_that_declares_the_earlier_direction_abandoned_merges_none_of_it()
    {
        // The sibling trajectory above with ONE plan-payload field flipped: plan(2) → spawn×2 → plan(1, ABANDON) →
        // merge. Conservation answers "re-planned AFTER the work landed"; it must not also answer "re-planned BECAUSE
        // the work was the wrong direction", and only the model can tell those apart — so the discard is its explicit
        // declaration, and the merge that follows folds none of what it discarded.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, "s1", "s2");
        await SeedSpawnAsync(runId, teamId, sequence: 2, Unit(a, "codespace/agent/a", acceptancePassed: true), Unit(b, "codespace/agent/b", acceptancePassed: true));
        await SeedAgentRunAsync(a, teamId, runId, "codespace/agent/a");
        await SeedAgentRunAsync(b, teamId, runId, "codespace/agent/b");
        await SeedPlanAsync(runId, teamId, sequence: 3, subtaskIds: new[] { "s3" }, abandonEarlierResults: true);

        var raw = (await RunMergeTurnAsync(runId, teamId))!;
        var outcome = JsonDocument.Parse(raw).RootElement;

        outcome.GetProperty("count").GetInt32().ShouldBe(0, "the plan changed direction — its predecessors' branches must not reach the reviewable head");
        raw.ShouldNotContain("codespace/agent/a", customMessage: "an abandoned contributor must not appear in the merge outcome at all");
        raw.ShouldNotContain("codespace/agent/b", customMessage: "an abandoned contributor must not appear in the merge outcome at all");
        outcome.TryGetProperty("carriedOverFromEarlierGenerations", out _).ShouldBeFalse("nothing was conserved — claiming a carry-over here would be the prompt's revoked promise all over again");
    }

    [Fact]
    public async Task An_abandoning_plan_records_how_much_finished_work_it_discarded()
    {
        // The discard's own receipt, through the REAL plan executor: a discard nobody can read back off the ledger is
        // indistinguishable from the silent loss the whole carry-over ladder exists to end.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, "s1", "s2");
        await SeedSpawnAsync(runId, teamId, sequence: 2, Unit(a, "codespace/agent/a", acceptancePassed: true), Unit(b, "codespace/agent/b", acceptancePassed: true));
        await SeedAgentRunAsync(a, teamId, runId, "codespace/agent/a");
        await SeedAgentRunAsync(b, teamId, runId, "codespace/agent/b");

        var plan = JsonDocument.Parse((await RunAbandoningPlanTurnAsync(runId, teamId))!).RootElement;

        plan.GetProperty("abandonedEarlierResults").GetInt32().ShouldBe(2, "the plan's own ledger row states how many finished results it took off the merge/publish floor");
        plan.GetProperty("count").GetInt32().ShouldBe(1, "the receipt is layered onto the ordinary plan outcome, never in place of it");
    }

    [Fact]
    public async Task A_carried_over_result_an_earlier_merge_already_consolidated_is_not_merged_twice()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        var already = Guid.NewGuid();
        var stranded = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, "s1", "s2");
        await SeedSpawnAsync(runId, teamId, sequence: 2, Unit(already, "codespace/agent/already", acceptancePassed: true), Unit(stranded, "codespace/agent/stranded", acceptancePassed: true));
        await SeedAgentRunAsync(already, teamId, runId, "codespace/agent/already");
        await SeedAgentRunAsync(stranded, teamId, runId, "codespace/agent/stranded");
        await SeedEarlierMergeAsync(runId, teamId, sequence: 3, already);
        await SeedPlanAsync(runId, teamId, sequence: 4, "s3");

        var outcome = JsonDocument.Parse((await RunMergeTurnAsync(runId, teamId))!).RootElement;

        outcome.GetProperty("merged").EnumerateArray().Select(e => e.GetProperty("producedBranch").GetString())
            .ShouldBe(new[] { "codespace/agent/stranded" }, "the ledger's own merge outcomes say what is already consolidated — no second table, no double fold");
        outcome.GetProperty("carriedOverFromEarlierGenerations").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_recorded_missing_agent_is_bounded_evidence_and_forced_integration_is_partial()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var present = Guid.NewGuid();
        var missing = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 1, Unit(present, "codespace/agent/present", null), Unit(missing, "codespace/agent/missing", null));
        await SeedAgentRunAsync(present, teamId, runId, "codespace/agent/present");

        var outcome = JsonDocument.Parse((await RunMergeTurnAsync(runId, teamId, forcedByPublishGate: true))!).RootElement;

        outcome.GetProperty("count").GetInt32().ShouldBe(1);
        var integrity = outcome.GetProperty("contributorIntegrity");
        integrity.GetProperty("status").GetString().ShouldBe("NeedsReview");
        integrity.GetProperty("expectedCount").GetInt32().ShouldBe(2);
        integrity.GetProperty("materializedCount").GetInt32().ShouldBe(1);
        var issue = integrity.GetProperty("issues").EnumerateArray().ShouldHaveSingleItem();
        issue.GetProperty("agentRunId").GetGuid().ShouldBe(missing);
        issue.GetProperty("kind").GetString().ShouldBe("MissingRow");
        outcome.GetProperty("integration").GetProperty("status").GetString().ShouldBe("Partial", "a subset must never integrate Clean when a recorded contributor is absent");
    }

    [Fact]
    public async Task A_cross_team_agent_id_is_named_without_reading_its_result_into_the_merge()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (otherTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var crossTeamId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 1, Unit(crossTeamId, "codespace/agent/foreign", null));
        await SeedAgentRunAsync(crossTeamId, otherTeamId, runId, "codespace/agent/foreign");

        var raw = (await RunMergeTurnAsync(runId, teamId))!;
        var outcome = JsonDocument.Parse(raw).RootElement;

        outcome.GetProperty("merged").GetArrayLength().ShouldBe(0);
        var issue = outcome.GetProperty("contributorIntegrity").GetProperty("issues").EnumerateArray().ShouldHaveSingleItem();
        issue.GetProperty("agentRunId").GetGuid().ShouldBe(crossTeamId);
        issue.GetProperty("kind").GetString().ShouldBe("CrossTeam");
        raw.ShouldNotContain("codespace/agent/foreign", customMessage: "the bounded fact never copies a foreign tenant's result payload");
        outcome.GetProperty("integration").GetProperty("status").GetString().ShouldBe("Partial");
    }

    [Fact]
    public async Task Malformed_non_terminal_missing_success_and_status_mismatch_rows_are_never_silently_dropped()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var malformed = Guid.NewGuid();
        var nonTerminal = Guid.NewGuid();
        var missingSuccess = Guid.NewGuid();
        var mismatch = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 1,
            Unit(malformed, "b-malformed", null), Unit(nonTerminal, "b-running", null), Unit(missingSuccess, "b-missing", null), Unit(mismatch, "b-mismatch", null));
        await SeedAgentRunRawAsync(malformed, teamId, runId, AgentRunStatus.Succeeded, """{"status":"notAStatus","exitReason":"x"}""");
        await SeedAgentRunRawAsync(nonTerminal, teamId, runId, AgentRunStatus.Running, null);
        await SeedAgentRunRawAsync(missingSuccess, teamId, runId, AgentRunStatus.Succeeded, null);
        await SeedAgentRunRawAsync(mismatch, teamId, runId, AgentRunStatus.Succeeded, ResultJson(AgentRunStatus.Failed));

        var raw = (await RunMergeTurnAsync(runId, teamId))!;
        var outcome = JsonDocument.Parse(raw).RootElement;
        var issues = outcome.GetProperty("contributorIntegrity").GetProperty("issues").EnumerateArray()
            .ToDictionary(i => i.GetProperty("agentRunId").GetGuid(), i => i.GetProperty("kind").GetString());

        issues.ShouldBe(new Dictionary<Guid, string?>
        {
            [malformed] = "MalformedResult",
            [nonTerminal] = "NonTerminalRow",
            [missingSuccess] = "MissingRequiredResult",
            [mismatch] = "ResultStatusMismatch",
        });
        outcome.GetProperty("count").GetInt32().ShouldBe(0);
        outcome.GetProperty("integration").GetProperty("status").GetString().ShouldBe("Partial");
        raw.ShouldNotContain("notAStatus", customMessage: "the integrity outcome carries only bounded enum facts, never malformed result bodies");
    }

    [Fact]
    public async Task A_missing_required_patch_fails_closed_and_terminalizes_the_merge_decision()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var agentRunId = Guid.NewGuid();
        var missingArtifactId = Guid.NewGuid();

        await SeedSpawnAsync(runId, teamId, sequence: 1, Unit(agentRunId, "codespace/agent/missing", null));
        await SeedAgentRunAsync(agentRunId, teamId, runId, "codespace/agent/missing", missingArtifactId);

        var ex = await Should.ThrowAsync<ArtifactContentUnavailableException>(() => RunMergeTurnAsync(runId, teamId));
        ex.Kind.ShouldBe(ArtifactContentUnavailableKind.MetadataMissing);
        ex.ArtifactId.ShouldBe(missingArtifactId);

        using var verify = _fixture.BeginScope();
        var decision = await verify.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .SingleAsync(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Merge);
        decision.Status.ShouldBe(SupervisorDecisionStatus.Failed, "the merge cannot remain Running and crash again forever");
        decision.Error.ShouldContain(missingArtifactId.ToString());
        decision.Error.ShouldNotContain("/var/", customMessage: "host filesystem paths are never the operator-facing failure contract");
    }

    // ─── Helpers ───

    private static SupervisorAgentResult Unit(Guid agentRunId, string producedBranch, bool? acceptancePassed) =>
        new() { AgentRunId = agentRunId, Status = "Succeeded", Summary = "did it", ProducedBranch = producedBranch, AcceptancePassed = acceptancePassed };

    private Task<string?> RunMergeTurnAsync(Guid runId, Guid teamId, bool forcedByPublishGate = false) =>
        RunTurnAsync(runId, teamId, new MergeDecider(forcedByPublishGate), SupervisorDecisionKinds.Merge);

    /// <summary>Drive ONE real plan turn whose payload declares the earlier generations' work abandoned — the model-authored signal, through the real executor.</summary>
    private Task<string?> RunAbandoningPlanTurnAsync(Guid runId, Guid teamId) =>
        RunTurnAsync(runId, teamId, new AbandoningPlanDecider(), SupervisorDecisionKinds.Plan);

    private async Task<string?> RunTurnAsync(Guid runId, Guid teamId, ISupervisorDecider decider, string decisionKind)
    {
        using (var scope = _fixture.BeginScope())
        {
            var service = new SupervisorTurnService(
                scope.Resolve<ISupervisorDecisionLog>(),
                decider,
                scope.Resolve<ISupervisorActionExecutor>(),
                scope.Resolve<CodeSpaceDbContext>(),
                scope.Resolve<ISupervisorAcceptanceGrader>(),
                scope.Resolve<IDecisionQueueService>(),
                scope.Resolve<IDecisionArbiter>(),
                scope.Resolve<IDecisionAnswerService>(),
                scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
                scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Supervisor.ISupervisorPublishedBranchResolver>(), scope.Resolve<CodeSpace.Core.Services.Completion.ICompletionAssessmentComposer>(), new AdmitAllBudgetLedger(),
        scope.Resolve<CodeSpace.Core.Services.Learning.ILessonReader>(),
        scope.Resolve<ILogger<SupervisorTurnService>>());

            await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, GoalConfig(), CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        return await verify.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == decisionKind)
            .OrderByDescending(d => d.Sequence)
            .Select(d => d.OutcomeJson)
            .FirstAsync();   // the turn this call just ran — a tape that already carried an earlier one of this kind has two
    }

    private async Task SeedSpawnAsync(Guid runId, Guid teamId, int sequence, params SupervisorAgentResult[] units)
    {
        var ids = units.Select(u => u.AgentRunId).ToArray();
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options), units);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
            DecisionKind = SupervisorDecisionKinds.Spawn, IdempotencyKey = $"spawn-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = """{"subtaskIds":["s1","s2"]}""", OutcomeJson = outcome,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A structurally-valid, non-empty plan — the boundary <see cref="SupervisorPlanWindow"/> opens a generation on, so a plan seeded AFTER a spawn slices that spawn out of the window.</summary>
    private Task SeedPlanAsync(Guid runId, Guid teamId, int sequence, params string[] subtaskIds) =>
        SeedPlanAsync(runId, teamId, sequence, subtaskIds, abandonEarlierResults: false);

    private async Task SeedPlanAsync(Guid runId, Guid teamId, int sequence, string[] subtaskIds, bool abandonEarlierResults)
    {
        var payload = JsonSerializer.Serialize(new SupervisorPlanPayload
        {
            Goal = Goal,
            Subtasks = subtaskIds.Select(id => new SupervisorPlannedSubtask { Id = id, Title = id, Instruction = $"do {id}" }).ToArray(),
            AbandonEarlierResults = abandonEarlierResults,
        }, AgentJson.Options);

        await SeedDecisionAsync(runId, teamId, sequence, SupervisorDecisionKinds.Plan, payload, "{}");
    }

    /// <summary>An earlier <c>merge</c> that already consolidated the named agent runs — the ledger fact the carry-over reads to avoid folding the same result twice.</summary>
    private async Task SeedEarlierMergeAsync(Guid runId, Guid teamId, int sequence, params Guid[] agentRunIds)
    {
        var outcome = JsonSerializer.Serialize(new
        {
            merged = agentRunIds.Select(id => new { agentRunId = id, status = nameof(AgentRunStatus.Succeeded) }),
            count = agentRunIds.Length,
        }, AgentJson.Options);

        await SeedDecisionAsync(runId, teamId, sequence, SupervisorDecisionKinds.Merge, "{}", outcome);
    }

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, int sequence, string kind, string payloadJson, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
            DecisionKind = kind, IdempotencyKey = $"{kind}-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentRunAsync(Guid agentRunId, Guid teamId, Guid runId, string producedBranch, Guid? patchArtifactId = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var resultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = "did it",
            ChangedFiles = new[] { "a.cs" }, ProducedBranch = producedBranch, PatchArtifactId = patchArtifactId,
        }, AgentJson.Options);

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = NodeId, Harness = "codex-cli",
            Status = AgentRunStatus.Succeeded, TaskJson = "{}", ResultJson = resultJson,
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedAgentRunRawAsync(Guid agentRunId, Guid teamId, Guid runId, AgentRunStatus status, string? resultJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId, TeamId = teamId, WorkflowRunId = runId, NodeId = NodeId, Harness = "codex-cli",
            Status = status, TaskJson = "{}", ResultJson = resultJson,
        });
        await db.SaveChangesAsync();
    }

    private static string ResultJson(AgentRunStatus status) => JsonSerializer.Serialize(new AgentRunResult { Status = status, ExitReason = "test" }, AgentJson.Options);

    private static SupervisorGoalConfig GoalConfig() => new() { Goal = Goal, AgentProfile = new SupervisorAgentProfile { RepositoryId = Guid.NewGuid() } };

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-merge-withhold-" + Guid.NewGuid().ToString("N")[..6],
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

    /// <summary>A decider that emits a single PLAN decision declaring every earlier generation's finished work abandoned — the direction-change re-plan.</summary>
    private sealed class AbandoningPlanDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Plan,
                PayloadJson = JsonSerializer.Serialize(new SupervisorPlanPayload
                {
                    Goal = Goal,
                    Subtasks = new[] { new SupervisorPlannedSubtask { Id = "s3", Title = "s3", Instruction = "start over, the other way" } },
                    AbandonEarlierResults = true,
                }, AgentJson.Options),
            });
    }

    /// <summary>A decider that emits a single MERGE decision — drives the real merge executor over the seeded prior spawn.</summary>
    private sealed class MergeDecider(bool forcedByPublishGate) : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Merge,
                PayloadJson = JsonSerializer.Serialize(new SupervisorMergePayload { SynthesisInstruction = "combine", ForcedByPublishGate = forcedByPublishGate }, AgentJson.Options),
            });
    }
}
