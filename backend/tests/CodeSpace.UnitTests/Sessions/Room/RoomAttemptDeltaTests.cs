using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Cost;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// Item 7.2 — a rerun used to tell the reader only THAT a retry happened, never WHAT changed. These pin the two pure
/// folds behind the ladder's "since previous attempt" line: <see cref="RoomProjector.FoldAttemptOutcomeFacts"/> (one
/// attempt's model / priced spend / acceptance grade, off its own agents' compact results) and
/// <see cref="RoomProjector.AttemptDeltaOf"/> (what differs between two consecutive attempts' facts).
///
/// <para>Tier: Unit — both are pure over already-folded <see cref="SupervisorAgentResult"/> / <see cref="SessionTurnAttempt"/>
/// facts, exactly like <see cref="RoomProjector.ResultVerdict"/> and <see cref="RoomProjector.UnitGrades"/> beside them.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RoomAttemptDeltaTests
{
    // ── FoldAttemptOutcomeFacts: one attempt's model / cost / acceptance off its own agents ──

    [Fact]
    public void A_single_shared_model_across_every_agent_is_the_attempts_model()
    {
        var facts = RoomProjector.FoldAttemptOutcomeFacts(new[] { Agent(model: "claude-sonnet-4-6"), Agent(model: "claude-sonnet-4-6") });

        facts.Model.ShouldBe("claude-sonnet-4-6");
    }

    [Fact]
    public void Different_models_across_agents_fold_to_unknown_never_a_guess()
    {
        // A mixed roster (a supervisor spawning agents on different tiers) has no ONE model to report — picking either
        // would be a confident lie about which one this attempt "used".
        var facts = RoomProjector.FoldAttemptOutcomeFacts(new[] { Agent(model: "claude-sonnet-4-6"), Agent(model: "claude-opus-4-8") });

        facts.Model.ShouldBeNull();
    }

    [Fact]
    public void Cost_sums_only_the_priceable_agents_and_is_null_when_none_priced()
    {
        var priced = RoomProjector.FoldAttemptOutcomeFacts(new[] { Agent(model: "claude-sonnet-4-6", inputTokens: 1000, outputTokens: 500) });
        priced.CostUsd.ShouldBe(AgentCostPricing.CostUsd("claude-sonnet-4-6", 1000, 500));

        var unpriceable = RoomProjector.FoldAttemptOutcomeFacts(new[] { Agent(model: "some-unknown-model", inputTokens: 1000, outputTokens: 500) });
        unpriceable.CostUsd.ShouldBeNull("an unpriceable model must read as unknown spend, never a misleading $0");
    }

    [Fact]
    public void No_graded_agent_folds_to_null_not_a_verdict()
    {
        RoomProjector.FoldAttemptOutcomeFacts(new[] { Agent(acceptancePassed: null), Agent(acceptancePassed: null) }).AcceptancePassed.ShouldBeNull();
        RoomProjector.FoldAttemptOutcomeFacts(Array.Empty<SupervisorAgentResult>()).AcceptancePassed.ShouldBeNull();
    }

    [Fact]
    public void A_vacuous_pass_is_excluded_from_the_attempts_acceptance_fold()
    {
        // "no changes were expected and none were produced" is satisfied BY CONSTRUCTION — nothing ran, so counting it
        // would launder "nothing to do" into "checked and correct" (mirrors RoomProjector.UnitGrades's own exclusion).
        var facts = RoomProjector.FoldAttemptOutcomeFacts(new[] { Agent(acceptancePassed: true, acceptanceDetail: AgentAcceptanceContract.NotApplicableDetail) });

        facts.AcceptancePassed.ShouldBeNull("no check executed, so this attempt has nothing graded to report");
    }

    [Fact]
    public void One_rejected_agent_fails_the_whole_attempt_and_carries_its_detail()
    {
        var facts = RoomProjector.FoldAttemptOutcomeFacts(new[]
        {
            Agent(acceptancePassed: true, acceptanceDetail: "tests-passed"),
            Agent(acceptancePassed: false, acceptanceDetail: "tests-failed-exit-1"),
        });

        facts.AcceptancePassed.ShouldBe(false);
        facts.AcceptanceDetail.ShouldBe("tests-failed-exit-1", "the REJECTING agent's own detail is the one worth reading");
    }

    [Fact]
    public void All_graded_agents_passing_folds_to_a_pass()
    {
        var facts = RoomProjector.FoldAttemptOutcomeFacts(new[] { Agent(acceptancePassed: true), Agent(acceptancePassed: true) });

        facts.AcceptancePassed.ShouldBe(true);
    }

    // ── AttemptDeltaOf: what differs between two consecutive attempts ──

    [Fact]
    public void Nothing_differs_the_whole_delta_is_null()
    {
        var previous = Attempt(WorkflowRunStatus.Success);
        var current = Attempt(WorkflowRunStatus.Success);
        var facts = new RoomProjector.AttemptOutcomeFacts("claude-sonnet-4-6", 0.05m, true, "tests-passed");

        RoomProjector.AttemptDeltaOf(previous, current, facts, facts).ShouldBeNull("a rerun that changed nothing comparable has no line to show");
    }

    [Fact]
    public void A_changed_model_is_the_only_field_the_delta_carries()
    {
        var previous = Attempt(WorkflowRunStatus.Success);
        var current = Attempt(WorkflowRunStatus.Success);
        var before = new RoomProjector.AttemptOutcomeFacts("claude-sonnet-4-6", 0.05m, true, null);
        var after = before with { Model = "claude-opus-4-8" };

        var delta = RoomProjector.AttemptDeltaOf(previous, current, before, after);

        delta.ShouldNotBeNull();
        delta!.Model.ShouldBe("claude-opus-4-8");
        delta.Outcome.ShouldBeNull();
        delta.AcceptancePassed.ShouldBeNull();
        delta.CostDeltaUsd.ShouldBeNull();
    }

    [Fact]
    public void A_model_unknown_on_either_side_never_reports_a_change()
    {
        var previous = Attempt(WorkflowRunStatus.Success);
        var current = Attempt(WorkflowRunStatus.Success);
        var unknown = new RoomProjector.AttemptOutcomeFacts(null, null, null, null);
        var known = unknown with { Model = "claude-opus-4-8" };

        // Nothing else differs either, so a KNOWN-to-unknown (or unknown-to-KNOWN) model is not a confident claim —
        // the whole delta reads as "nothing changed", not a half-populated guess.
        RoomProjector.AttemptDeltaOf(previous, current, unknown, known).ShouldBeNull("either side unknown is not a confident claim");
        RoomProjector.AttemptDeltaOf(previous, current, known, unknown).ShouldBeNull();
    }

    [Fact]
    public void A_changed_status_is_reported_as_the_outcome()
    {
        var previous = Attempt(WorkflowRunStatus.Failure);
        var current = Attempt(WorkflowRunStatus.Success);
        var facts = new RoomProjector.AttemptOutcomeFacts(null, null, null, null);

        RoomProjector.AttemptDeltaOf(previous, current, facts, facts)!.Outcome.ShouldBe(WorkflowRunStatus.Success);
    }

    [Fact]
    public void A_completion_park_is_an_outcome_change_even_though_the_status_stays_Suspended()
    {
        // The one case two attempts can share a raw status yet mean something different: a park refuses the terminal
        // and waits on nobody, while a plain Suspended waits on an approval / timer that IS coming.
        var now = DateTimeOffset.UtcNow;
        var waiting = Attempt(WorkflowRunStatus.Suspended);
        var parked = Attempt(WorkflowRunStatus.Suspended) with { CompletionParkedAt = now };
        var facts = new RoomProjector.AttemptOutcomeFacts(null, null, null, null);

        RoomProjector.AttemptDeltaOf(waiting, parked, facts, facts)!.Outcome.ShouldBe(WorkflowRunStatus.Suspended, "the enum is unchanged, but parked-ness flipped — still worth a line");
        RoomProjector.AttemptDeltaOf(parked, parked, facts, facts).ShouldBeNull("both parked ⇒ nothing changed");
    }

    [Fact]
    public void An_acceptance_flip_carries_the_new_verdict_and_its_grader_detail()
    {
        var previous = Attempt(WorkflowRunStatus.Success);
        var current = Attempt(WorkflowRunStatus.Success);
        var before = new RoomProjector.AttemptOutcomeFacts(null, null, true, "tests-passed");
        var after = new RoomProjector.AttemptOutcomeFacts(null, null, false, "tests-failed-exit-1");

        var delta = RoomProjector.AttemptDeltaOf(previous, current, before, after);

        delta!.AcceptancePassed.ShouldBe(false);
        delta.AcceptanceDetail.ShouldBe("tests-failed-exit-1", "the NEW attempt's own grader detail explains the flip");
    }

    [Fact]
    public void An_acceptance_grade_absent_on_either_side_never_reports_a_flip()
    {
        var previous = Attempt(WorkflowRunStatus.Success);
        var current = Attempt(WorkflowRunStatus.Success);
        var ungraded = new RoomProjector.AttemptOutcomeFacts(null, null, null, null);
        var graded = ungraded with { AcceptancePassed = false, AcceptanceDetail = "tests-failed-exit-1" };

        // Nothing else differs either: an attempt that STARTED grading is an absence-to-verdict transition, not a
        // flip between two verdicts, so it must not manufacture a line out of one side having no grade at all.
        RoomProjector.AttemptDeltaOf(previous, current, ungraded, graded).ShouldBeNull("nothing graded on the earlier attempt is an absence, not a flip");
    }

    [Theory]
    [InlineData(0.0105, 0.035, 0.0245)]     // a costlier rerun (a stronger model / more tokens)
    [InlineData(0.035, 0.0105, -0.0245)]    // a cheaper rerun
    public void The_cost_delta_is_signed_current_minus_previous(double previousUsd, double currentUsd, double expectedDeltaUsd)
    {
        var previous = Attempt(WorkflowRunStatus.Success);
        var current = Attempt(WorkflowRunStatus.Success);
        var before = new RoomProjector.AttemptOutcomeFacts(null, (decimal)previousUsd, null, null);
        var after = new RoomProjector.AttemptOutcomeFacts(null, (decimal)currentUsd, null, null);

        RoomProjector.AttemptDeltaOf(previous, current, before, after)!.CostDeltaUsd.ShouldBe((decimal)expectedDeltaUsd);
    }

    [Fact]
    public void A_cost_unpriceable_on_either_side_never_reports_a_delta()
    {
        var previous = Attempt(WorkflowRunStatus.Success);
        var current = Attempt(WorkflowRunStatus.Success);
        var unpriced = new RoomProjector.AttemptOutcomeFacts(null, null, null, null);
        var priced = unpriced with { CostUsd = 0.05m };

        RoomProjector.AttemptDeltaOf(previous, current, unpriced, priced).ShouldBeNull("an unpriceable side must never manufacture a delta out of a single known number");
    }

    private static SupervisorAgentResult Agent(string? model = "claude-sonnet-4-6", int inputTokens = 0, int outputTokens = 0, bool? acceptancePassed = null, string? acceptanceDetail = null) =>
        new() { AgentRunId = Guid.NewGuid(), Status = "Succeeded", Model = model, InputTokens = inputTokens, OutputTokens = outputTokens, AcceptancePassed = acceptancePassed, AcceptanceDetail = acceptanceDetail };

    private static SessionTurnAttempt Attempt(WorkflowRunStatus status) =>
        new() { RunId = Guid.NewGuid(), AttemptNumber = 1, Status = status, SourceType = "rerun", CreatedDate = DateTimeOffset.UtcNow, IsLatest = true };
}
