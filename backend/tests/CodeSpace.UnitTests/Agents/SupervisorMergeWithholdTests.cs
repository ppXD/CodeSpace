using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: loopability slice 4 ("局部綠≠整合綠") — the merge withholds a per-unit-REJECTED unit's branch. A unit that
/// failed its OWN definition-of-done (slice 3, <see cref="SupervisorAgentResult.AcceptancePassed"/> == false) must not
/// be integrated into the reviewable head, even if the model merges. Pins <see cref="RealSupervisorActionExecutor.ResolveAgentRunIdsToMerge"/>:
/// a rejected unit's id is excluded, a passing/ungraded unit's is kept, a retry (fresh id) integrates while the rejected
/// original is withheld, and an all-ungraded wave is byte-identical (every id kept — the pre-slice behaviour).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorMergeWithholdTests
{
    private static SupervisorAgentResult Unit(bool? acceptancePassed) =>
        new() { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "codespace/agent/x", AcceptancePassed = acceptancePassed };

    private static SupervisorPriorDecision Staging(string kind, params SupervisorAgentResult[] units)
    {
        var ids = units.Select(u => u.AgentRunId).ToArray();
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options), units);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome };
    }

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) => new() { Goal = "g", PriorDecisions = prior };

    [Fact]
    public void A_rejected_unit_is_withheld_while_passing_and_ungraded_units_integrate()
    {
        var passed = Unit(true);
        var rejected = Unit(false);
        var ungraded = Unit(null);

        var toMerge = RealSupervisorActionExecutor.ResolveAgentRunIdsToMerge(Context(Staging(SupervisorDecisionKinds.Spawn, passed, rejected, ungraded)));

        toMerge.ShouldBe(new[] { passed.AgentRunId, ungraded.AgentRunId }, "the rejected unit's branch is withheld from the merge; passing + ungraded integrate");
    }

    [Fact]
    public void An_all_ungraded_wave_keeps_every_id_byte_identical_to_pre_slice()
    {
        var a = Unit(null);
        var b = Unit(null);

        RealSupervisorActionExecutor.ResolveAgentRunIdsToMerge(Context(Staging(SupervisorDecisionKinds.Spawn, a, b)))
            .ShouldBe(new[] { a.AgentRunId, b.AgentRunId }, "no per-unit verdicts → every staged id integrates, exactly as before the slice");
    }

    [Fact]
    public void A_retry_after_a_rejection_integrates_while_the_rejected_original_is_withheld()
    {
        // The original spawn's unit was rejected; a retry (a FRESH agent run id) passed → integrate the retry, withhold
        // the original. Resolving by agent-run id (not subtask id) makes this fall out for free.
        var rejectedOriginal = Unit(false);
        var passingRetry = Unit(true);

        var toMerge = RealSupervisorActionExecutor.ResolveAgentRunIdsToMerge(Context(
            Staging(SupervisorDecisionKinds.Spawn, rejectedOriginal),
            Staging(SupervisorDecisionKinds.Retry, passingRetry)));

        toMerge.ShouldBe(new[] { passingRetry.AgentRunId }, "the rejected original is withheld; its passing retry integrates");
    }

    [Fact]
    public void An_all_rejected_wave_integrates_nothing()
    {
        RealSupervisorActionExecutor.ResolveAgentRunIdsToMerge(Context(Staging(SupervisorDecisionKinds.Spawn, Unit(false), Unit(false))))
            .ShouldBeEmpty("every unit failed its own acceptance → there is nothing accepted to integrate");
    }

    [Fact]
    public void A_context_with_no_staging_decisions_resolves_to_empty()
    {
        RealSupervisorActionExecutor.ResolveAgentRunIdsToMerge(Context()).ShouldBeEmpty();
    }

    // ── The OTHER door to the reviewable head: the resolver's branch set must withhold the same rejected units, else a
    //    conflict→resolve reconciles a rejected branch back into the head (the review finding this slice folds). ──

    private static SupervisorAgentResult UnitWithBranch(string branch, bool? acceptancePassed) =>
        new() { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = branch, AcceptancePassed = acceptancePassed };

    [Fact]
    public void The_resolver_branch_set_withholds_a_rejected_units_branch()
    {
        var context = Context(Staging(SupervisorDecisionKinds.Spawn,
            UnitWithBranch("b-passed", true), UnitWithBranch("b-rejected", false), UnitWithBranch("b-ungraded", null)));

        RealSupervisorActionExecutor.CollectAgentBranches(context)
            .ShouldBe(new[] { "b-passed", "b-ungraded" }, "the resolver reconciles only the non-rejected branches — the same withhold as the merge, closing the second door to the head");
    }

    [Fact]
    public void The_resolver_branch_set_is_byte_identical_for_an_all_ungraded_wave()
    {
        var context = Context(Staging(SupervisorDecisionKinds.Spawn, UnitWithBranch("b-a", null), UnitWithBranch("b-b", null)));

        RealSupervisorActionExecutor.CollectAgentBranches(context).ShouldBe(new[] { "b-a", "b-b" }, "no verdicts → every branch is reconciled, exactly as before the slice");
    }

    [Fact]
    public void The_resolver_per_repo_branch_set_withholds_a_rejected_unit()
    {
        var repo = Guid.NewGuid();

        SupervisorAgentResult MultiRepoUnit(string branch, bool? passed) => new()
        {
            AgentRunId = Guid.NewGuid(), Status = "Succeeded", AcceptancePassed = passed,
            RepositoryResults = new[] { new RepositoryRunResult { Alias = "r", RepositoryId = repo, ProducedBranch = branch, BaseBranch = "main", Access = WorkspaceAccess.Write } },
        };

        var context = Context(Staging(SupervisorDecisionKinds.Spawn, MultiRepoUnit("r-passed", true), MultiRepoUnit("r-rejected", false)));

        RealSupervisorActionExecutor.CollectAgentBranchesForRepo(context, repo)
            .ShouldBe(new[] { "r-passed" }, "the per-repo resolver set withholds a rejected unit too (defensive — multi-repo per-unit grades are deferred, so today rejected multi-repo units don't arise, but the guard holds)");
    }
}
