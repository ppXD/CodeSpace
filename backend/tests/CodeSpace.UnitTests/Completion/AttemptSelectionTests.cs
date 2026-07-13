using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the generic attempt projection + the TWO selectors (P1b / Lock Clause 3) — the only attempt-rule
/// authorities. Ports the retired supervisor-specific selector\u2019s pins (retried-then-passed, ledger ordering,
/// positional join, non-staging ignored) onto the generic shape, and pins the new law: a newly AUTHORIZED attempt
/// supersedes operationally even before it settles; metric @1 always reads the FIRST authorized attempt;
/// unit keys are plan-version-aware; positional mismatch is a reported contract error, never silent truncation.
/// </summary>
[Trait("Category", "Unit")]
public class AttemptSelectionTests
{
    [Fact]
    public void A_retried_then_passed_unit_selects_the_retry_operationally_and_the_first_attempt_for_metrics()
    {
        var decisions = new[]
        {
            Plan(seq: 0, out var planId),
            Spawn(seq: 1, new[] { "s1" }, Settled("first")),
            Retry(seq: 2, "s1", Settled("second")),
        };

        var set = SupervisorAttemptAdapter.Project(decisions);
        set.ContractErrors.ShouldBeEmpty();

        var key = new UnitKey(planId, 1, "s1");
        AttemptSelectors.SelectOperationalActive(set.Attempts)[key].AttemptOrdinal.ShouldBe(2, "the superseded failure\u2019s evidence must never reach a fold");
        AttemptSelectors.SelectFirstAuthorized(set.Attempts)[key].AttemptOrdinal.ShouldBe(1, "VDS@1 reads the FIRST authorized attempt, never best-of-N");
    }

    [Fact]
    public void A_newly_authorized_attempt_supersedes_before_it_settles()
    {
        // Lock Clause 3: once the server authorizes a new attempt, the old Passed attempt is no longer the
        // operational attempt — a consumer must wait or park, never terminalize off the superseded receipt.
        var decisions = new[]
        {
            Plan(seq: 0, out var planId),
            Spawn(seq: 1, new[] { "s1" }, Settled("passed")),
            RunningRetry(seq: 2, "s1", stagedAttemptId: Guid.NewGuid()),
        };

        var set = SupervisorAttemptAdapter.Project(decisions);

        var active = AttemptSelectors.SelectOperationalActive(set.Attempts)[new UnitKey(planId, 1, "s1")];
        active.State.ShouldBe(AttemptState.Authorized);
        active.AttemptOrdinal.ShouldBe(2);
    }

    [Fact]
    public void Unit_keys_are_plan_version_aware()
    {
        // s1@plan-v1 never satisfies s1@plan-v2 — a replan mints a NEW unit stream with its own ordinals.
        var decisions = new[]
        {
            Plan(seq: 0, out var planId),
            Spawn(seq: 1, new[] { "s1" }, Settled("v1-attempt")),
            PlanV2(seq: 2, planId),
            Spawn(seq: 3, new[] { "s1" }, Settled("v2-attempt")),
        };

        var set = SupervisorAttemptAdapter.Project(decisions);
        var operational = AttemptSelectors.SelectOperationalActive(set.Attempts);

        operational[new UnitKey(planId, 1, "s1")].AttemptOrdinal.ShouldBe(1);
        operational[new UnitKey(planId, 2, "s1")].AttemptOrdinal.ShouldBe(1, "the v2 stream starts its own ordinals");
        operational.Count.ShouldBe(2);
    }

    [Fact]
    public void A_positional_mismatch_is_a_reported_contract_error_never_silent()
    {
        var decisions = new[]
        {
            Plan(seq: 0, out _),
            Spawn(seq: 1, new[] { "s1", "s2" }, Settled("only-one")),
        };

        var set = SupervisorAttemptAdapter.Project(decisions);

        set.ContractErrors.ShouldHaveSingleItem().ShouldContain("positional contract broken");
        set.Attempts.Count.ShouldBe(1, "the aligned prefix still projects — the ERROR carries the truncation, not silence");
    }

    [Fact]
    public void The_dispatch_time_stamp_wins_over_tape_reconstruction()
    {
        var decisions = new[] { Plan(seq: 0, out var planId), Spawn(seq: 1, new[] { "s1" }, Settled("a")) };
        var attemptId = SupervisorOutcome.ReadAgentResults(decisions[1].OutcomeJson)[0].AgentRunId;
        var stamped = new WorkUnitRef { WorkPlanId = planId, PlanVersion = 1, UnitId = "s1", ContractHash = "sha256/canonical-json-v1:abc" };

        var set = SupervisorAttemptAdapter.Project(decisions, new Dictionary<Guid, WorkUnitRef> { [attemptId] = stamped });

        set.Attempts.Single().WorkUnit!.ContractHash.ShouldBe("sha256/canonical-json-v1:abc");
    }

    [Fact]
    public void Non_staging_decisions_and_idless_retries_are_ignored()
    {
        var decisions = new[]
        {
            Plan(seq: 0, out _),
            Spawn(seq: 1, new[] { "s1" }, Settled("kept")),
            new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = Outcome(Settled("noise")) },
            new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = Outcome(Settled("idless")) },
        };

        SupervisorAttemptAdapter.Project(decisions).Attempts.ShouldHaveSingleItem().UnitId.ShouldBe("s1");
    }

    [Fact]
    public void A_legacy_tape_without_plan_refs_still_groups_by_bare_unit_id()
    {
        var decisions = new[] { Spawn(seq: 1, new[] { "s1" }, Settled("legacy")) };

        var set = SupervisorAttemptAdapter.Project(decisions);

        var attempt = set.Attempts.ShouldHaveSingleItem();
        attempt.WorkUnit.ShouldBeNull("no plan ref on the tape and no stamp — Legacy/Shadow only (Lock Clause 3)");
        AttemptSelectors.SelectOperationalActive(set.Attempts).ContainsKey(new UnitKey(null, null, "s1")).ShouldBeTrue();
    }

    // \u2500\u2500 Builders \u2500\u2500

    private static SupervisorPriorDecision Plan(long seq, out Guid planId)
    {
        planId = Guid.NewGuid();
        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = seq, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = "{}",
            OutcomeJson = $$"""{"planned":[],"count":0,"workPlanId":"{{planId}}","workPlanVersion":1}""",
        };
    }

    private static SupervisorPriorDecision PlanV2(long seq, Guid planId) => new()
    {
        Id = Guid.NewGuid(), Sequence = seq, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = "{}",
        OutcomeJson = $$"""{"planned":[],"count":0,"workPlanId":"{{planId}}","workPlanVersion":2}""",
    };

    private static SupervisorPriorDecision Spawn(long seq, string[] subtaskIds, params SupervisorAgentResult[] results) => new()
    {
        Id = Guid.NewGuid(), Sequence = seq, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new { subtaskIds }),
        OutcomeJson = Outcome(results),
    };

    private static SupervisorPriorDecision Retry(long seq, string subtaskId, SupervisorAgentResult result) => new()
    {
        Id = Guid.NewGuid(), Sequence = seq, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = JsonSerializer.Serialize(new { subtaskId }),
        OutcomeJson = Outcome(result),
    };

    private static SupervisorPriorDecision RunningRetry(long seq, string subtaskId, Guid stagedAttemptId) => new()
    {
        Id = Guid.NewGuid(), Sequence = seq, DecisionKind = SupervisorDecisionKinds.Retry, Status = SupervisorDecisionStatus.Running,
        PayloadJson = JsonSerializer.Serialize(new { subtaskId }),
        OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = new[] { stagedAttemptId } }),
    };

    private static string Outcome(params SupervisorAgentResult[] results) =>
        JsonSerializer.Serialize(new { agentResults = results }, CodeSpace.Core.Services.Agents.AgentJson.Options);

    private static SupervisorAgentResult Settled(string summary) => new() { AgentRunId = Guid.NewGuid(), Status = "Succeeded", Summary = summary };
}
