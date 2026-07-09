using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the S1 handoff's PURE decision surface — which dependency a subtask's staging resolves against
/// (<see cref="RealSupervisorActionExecutor.DependsOnFor"/>) and the wire shape a staging BLOCK records
/// (<see cref="RealSupervisorActionExecutor.BuildBlockedSpawnOutcome"/>), round-tripped through the SAME
/// <see cref="SupervisorOutcome.ReadIntegration"/> a <c>merge</c> conflict is read through — the seam that
/// makes "a staging conflict is reconcilable by the EXISTING <c>resolve</c> verb" actually true. The async
/// manifest-read + real-git-integration half (<c>ResolveDependencyStagingAsync</c> / <c>IntegrateProducersAsync</c>)
/// is proven at the integration tier (real Postgres + a real bare git remote).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorDependencyStagingTests
{
    // ── DependsOnFor: BaseSubtaskId override precedence ─────────────────────────────────

    [Fact]
    public void A_dispatch_base_subtask_id_override_wins_over_the_plans_depends_on()
    {
        var planned = new SupervisorPlannedSubtask { Id = "b", Title = "b", Instruction = "do", DependsOn = new[] { "a1", "a2" } };
        var spec = new SupervisorAgentDispatch { SubtaskId = "b", BaseSubtaskId = "a2" };

        RealSupervisorActionExecutor.DependsOnFor(planned, spec).ShouldBe(new[] { "a2" }, "the model-authored override narrows to ONE producer for this spawn, even though the plan declared two");
    }

    [Fact]
    public void With_no_dispatch_override_the_plans_depends_on_stands()
    {
        var planned = new SupervisorPlannedSubtask { Id = "b", Title = "b", Instruction = "do", DependsOn = new[] { "a1", "a2" } };

        RealSupervisorActionExecutor.DependsOnFor(planned, spec: null).ShouldBe(new[] { "a1", "a2" });
        RealSupervisorActionExecutor.DependsOnFor(planned, new SupervisorAgentDispatch { SubtaskId = "b" }).ShouldBe(new[] { "a1", "a2" }, "a dispatch with no BaseSubtaskId leaves the plan's DependsOn untouched");
    }

    [Fact]
    public void With_neither_a_plan_dependency_nor_an_override_there_is_nothing_to_stage()
    {
        RealSupervisorActionExecutor.DependsOnFor(planned: null, spec: null).ShouldBeEmpty();
        RealSupervisorActionExecutor.DependsOnFor(new SupervisorPlannedSubtask { Id = "a", Title = "a", Instruction = "do" }, spec: null).ShouldBeEmpty("a flat subtask with no DependsOn resolves no dependency — byte-identical no-override path");
    }

    // ── PreferPriorAttemptStaging: retry world-state conservation's precedence (P0-1) ──

    [Fact]
    public void A_resolved_prior_attempt_ref_wins_over_a_plan_dependency_ref()
    {
        var priorAttempt = new DependencyStagingResult { Ref = "codespace/agent/prior-attempt", GoalFoldText = "prior attempt fold" };
        var dependency = new DependencyStagingResult { Ref = "codespace/agent/producer", GoalFoldText = "producer fold" };

        RealSupervisorActionExecutor.PreferPriorAttemptStaging(priorAttempt, dependency).ShouldBe(priorAttempt, "the retried subtask's OWN committed work is more specific than a plan dependency's handoff");
    }

    [Fact]
    public void With_no_prior_attempt_ref_the_dependency_staging_stands_unchanged()
    {
        var dependency = new DependencyStagingResult { Ref = "codespace/agent/producer", GoalFoldText = "producer fold" };

        RealSupervisorActionExecutor.PreferPriorAttemptStaging(DependencyStagingResult.NoOverride, dependency).ShouldBe(dependency, "no prior-attempt continuity → byte-identical to pre-P0-1 dependency staging");
    }

    [Fact]
    public void With_neither_resolved_the_result_is_still_NoOverride()
    {
        RealSupervisorActionExecutor.PreferPriorAttemptStaging(DependencyStagingResult.NoOverride, DependencyStagingResult.NoOverride).ShouldBe(DependencyStagingResult.NoOverride, "a genuine cold-start retry with no declared dependency clones the default branch");
    }

    // ── ApplyResumeRecord: the resume hint's TRUTH VALUE must match the actual git state (P0-1) ──

    [Fact]
    public void A_workspace_pinned_to_prior_work_carries_no_honest_redo_hint()
    {
        var task = new AgentTask { Goal = "do the thing", Harness = "codex-cli" };
        var prior = new ResumableSession(Guid.NewGuid(), "sess-1", "transcript", null);

        var resumed = RealSupervisorActionExecutor.ApplyResumeRecord(task, prior, workspaceHasPriorWork: true);

        resumed.ResumeFromSessionId.ShouldBe("sess-1");
        resumed.RestoredTranscript.ShouldBe("transcript");
        resumed.Goal.ShouldBe("do the thing", "the workspace genuinely contains the prior branch — no honesty caveat needed");
    }

    [Fact]
    public void A_workspace_with_no_preserved_git_state_gets_the_honest_redo_hint()
    {
        var task = new AgentTask { Goal = "do the thing", Harness = "codex-cli" };
        var prior = new ResumableSession(Guid.NewGuid(), "sess-1", "transcript", null);

        var resumed = RealSupervisorActionExecutor.ApplyResumeRecord(task, prior, workspaceHasPriorWork: false);

        resumed.ResumeFromSessionId.ShouldBe("sess-1", "the conversation is still restored");
        resumed.Goal.ShouldBe($"do the thing\n\n{RealSupervisorActionExecutor.HonestNoContinuityHint}", "the goal now HONESTLY says the git changes are NOT present, so the agent never trusts a restored conversation implying work it can't see");
    }

    // ── BuildBlockedSpawnOutcome: the wire shape resolve's conflict reader consumes ─────

    [Fact]
    public void A_missing_manifest_block_records_no_integration_detail()
    {
        var blocked = new[] { new RealSupervisorActionExecutor.DependencyBlock("b", "producer x recorded a diff but no patch was captured for it", Array.Empty<string>(), Array.Empty<string>()) };

        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildBlockedSpawnOutcome(blocked), AgentJson.Options);

        SupervisorOutcome.ReadStagedAgentCount(json).ShouldBe(0, "a blocked spawn stages zero agents");
        SupervisorOutcome.ReadIntegration(json).ShouldBeNull("a missing-manifest block is a data-integrity guard, not a reconcilable conflict — resolve has nothing to act on");
    }

    [Fact]
    public void A_conflicted_integration_block_round_trips_through_the_shared_integration_reader()
    {
        var blocked = new[]
        {
            new RealSupervisorActionExecutor.DependencyBlock("c", "the producers' work could not be auto-integrated onto one branch", new[] { "src/app.py" }, new[] { "codespace/agent/p1", "codespace/agent/p2" }),
        };

        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildBlockedSpawnOutcome(blocked), AgentJson.Options);

        var integration = SupervisorOutcome.ReadIntegration(json);

        integration.ShouldNotBeNull("a conflict-carrying block records an integration detail block");
        integration!.IsConflicted.ShouldBeTrue("the SAME status string a merge conflict uses, so the existing resolve verb's skip check recognizes it");
        integration.ConflictedFiles.ShouldBe(new[] { "src/app.py" });
        integration.PreservedBranches.ShouldBe(new[] { "codespace/agent/p1", "codespace/agent/p2" }, "both producers' own branches are named for the resolver to reconcile");
    }

    [Fact]
    public void Every_blocked_subtask_names_its_own_reason()
    {
        var blocked = new[]
        {
            new RealSupervisorActionExecutor.DependencyBlock("b", "reason one", Array.Empty<string>(), Array.Empty<string>()),
            new RealSupervisorActionExecutor.DependencyBlock("c", "reason two", Array.Empty<string>(), Array.Empty<string>()),
        };

        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildBlockedSpawnOutcome(blocked), AgentJson.Options);
        using var doc = JsonDocument.Parse(json);

        var entries = doc.RootElement.GetProperty("blockedSubtasks").EnumerateArray().ToList();

        entries.Select(e => e.GetProperty("subtaskId").GetString()).ShouldBe(new[] { "b", "c" });
        entries.Select(e => e.GetProperty("reason").GetString()).ShouldBe(new[] { "reason one", "reason two" }, "every withheld subtask is named with its OWN reason, never collapsed into one");
    }

    // ── BuildRejectedRetryOutcome: P0-2 action schema validation ────────────────────────

    [Fact]
    public void The_rejected_retry_outcome_names_the_specific_defect()
    {
        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildRejectedRetryOutcome(), AgentJson.Options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("retry").GetString().ShouldBe("rejected");
        doc.RootElement.GetProperty("reason").GetString().ShouldBe("the retry decision named no subtaskId — a retry must name the plan-local subtask id to re-run");
    }

    [Fact]
    public void A_rejected_retry_outcome_carries_no_agent_results_so_the_no_progress_watchdog_still_counts_it()
    {
        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.BuildRejectedRetryOutcome(), AgentJson.Options);

        SupervisorOutcome.HasSettledEvidence(SupervisorOutcome.ReadAgentResults(json)).ShouldBeFalse("the rejection reads as no settled evidence — the EXISTING no-progress streak already trips on this without any new counter");
    }

    // ── FindMostRecentConflictDecision: the widened Merge-OR-Spawn conflict source ─────

    [Fact]
    public void A_conflicted_spawn_decision_is_recognized_as_a_conflict_source()
    {
        var spawn = ConflictedDecision(SupervisorDecisionKinds.Spawn, sequence: 1);

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(spawn)).ShouldBe(spawn, "a staging-blocked spawn's integration block is a conflict source exactly like a merge's");
    }

    [Fact]
    public void A_conflicted_merge_decision_is_still_recognized_as_a_conflict_source()
    {
        var merge = ConflictedDecision(SupervisorDecisionKinds.Merge, sequence: 1);

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(merge)).ShouldBe(merge, "byte-identical to before the widening — a merge conflict is still recognized");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Retry)]
    [InlineData(SupervisorDecisionKinds.Resolve)]
    [InlineData(SupervisorDecisionKinds.Plan)]
    [InlineData(SupervisorDecisionKinds.AskHuman)]
    [InlineData(SupervisorDecisionKinds.Stop)]
    public void No_other_decision_kind_is_ever_misread_as_a_conflict_source_even_carrying_a_conflicted_integration_block(string kind)
    {
        // Defensive: only Merge and Spawn may ever surface a conflict. A conflicted-shaped integration block on any
        // OTHER kind (however it got there) must never be picked up — the widening is a NAMED allow-list, not "any
        // decision with this JSON shape".
        var decision = ConflictedDecision(kind, sequence: 1);

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(decision)).ShouldBeNull();
    }

    [Fact]
    public void The_most_recent_conflict_wins_across_a_mixed_merge_and_spawn_tape()
    {
        var olderMerge = ConflictedDecision(SupervisorDecisionKinds.Merge, sequence: 1);
        var newerSpawn = ConflictedDecision(SupervisorDecisionKinds.Spawn, sequence: 2);

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(olderMerge, newerSpawn)).ShouldBe(newerSpawn, "newest-first — the later staging block supersedes the earlier merge conflict");
    }

    [Fact]
    public void A_clean_spawn_with_no_integration_block_is_not_a_conflict_source()
    {
        var cleanSpawn = new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = "{\"agentRunIds\":[],\"agentCount\":0}" };

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(Context(cleanSpawn)).ShouldBeNull("an ordinary successful spawn carries no integration block at all");
    }

    private static SupervisorPriorDecision ConflictedDecision(string kind, int sequence) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
        OutcomeJson = JsonSerializer.Serialize(new { integration = new { status = "Conflicted", outcomes = Array.Empty<object>() } }, AgentJson.Options),
    };

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) => new() { Goal = "g", PriorDecisions = prior };
}
