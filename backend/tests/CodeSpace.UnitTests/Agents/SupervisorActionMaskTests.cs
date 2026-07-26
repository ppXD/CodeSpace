using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins A1.5's action mask v1 — the prompt block naming what CANNOT advance the run this turn. The verb
/// it covers is <c>resolve</c>, whose availability is a server-decided fact rather than a judgement: with no
/// recorded conflict the executor no-ops it (a wasted turn), and past the resolve cap the bounds FORCE-STOP the
/// whole run. Also pins the two properties that make a mask safe: it renders NOTHING when everything is available
/// (a healthy run's prompt stays byte-identical), and it reads the SAME conflict authority the resolve executor
/// acts on, so the two can never disagree about whether a conflict exists.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorActionMaskTests
{
    private static SupervisorPriorDecision Decision(long seq, string kind, string? outcomeJson = null) => new()
    {
        Id = Guid.NewGuid(), Sequence = seq, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = "{}", OutcomeJson = outcomeJson ?? "{}",
    };

    private static string ConflictedOutcome() => JsonSerializer.Serialize(new
    {
        integration = new { status = "Conflicted", conflictedFiles = new[] { "src/Foo.cs" }, preservedBranches = new[] { "codespace/agent/a" }, outcomes = Array.Empty<object>() },
    }, AgentJson.Options);

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) =>
        new() { Goal = "ship it", TurnNumber = prior.Length, PriorDecisions = prior };

    // ── The no-conflict arm: a resolve would be a no-op ──────────────────────────────

    [Fact]
    public void With_no_conflict_recorded_resolve_is_masked()
    {
        var mask = SupervisorActionMask.Render(Context(Decision(1, SupervisorDecisionKinds.Plan), Decision(2, SupervisorDecisionKinds.Spawn)));

        mask.ShouldNotBeNull("the rails now name the resolve verb, so a model can misfire it where nothing conflicts");
        mask!.ShouldContain("UNAVAILABLE THIS TURN", Case.Sensitive);
        mask.ShouldContain("resolve", Case.Sensitive);
        mask.ShouldContain("nothing to reconcile", Case.Insensitive);
    }

    [Fact]
    public void A_clean_merge_is_not_a_conflict()
    {
        var clean = JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch = "b", outcomes = Array.Empty<object>() } }, AgentJson.Options);

        SupervisorActionMask.Render(Context(Decision(1, SupervisorDecisionKinds.Merge, clean)))
            .ShouldNotBeNull("a clean integration leaves nothing to resolve");
    }

    // ── The available arm: nothing is masked, so nothing renders ─────────────────────

    [Fact]
    public void With_a_live_conflict_and_cap_remaining_the_block_renders_nothing()
    {
        // The byte-identity property: an available action set must not add a block. Eight optional prompt blocks
        // depend on this convention, and the auto-compaction / token-budget characteristics ride on it.
        SupervisorActionMask.Render(Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome())))
            .ShouldBeNull("resolve is genuinely available here — masking it would steer the model away from the right move");
    }

    [Fact]
    public void A_spawn_staging_conflict_also_makes_resolve_available()
    {
        // The widened source: a spawn whose dependency staging could not auto-integrate records the SAME integration
        // shape a merge does. Missing that would mask the one verb that reconciles it.
        SupervisorActionMask.Render(Context(Decision(1, SupervisorDecisionKinds.Spawn, ConflictedOutcome())))
            .ShouldBeNull();
    }

    // ── The cap arm: an over-cap resolve is not refused, it ends the run ─────────────

    [Fact]
    public void Past_the_resolve_cap_the_mask_names_the_run_ending_consequence()
    {
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome()), Decision(2, SupervisorDecisionKinds.Resolve)) with { MaxResolveAttempts = 1 };

        var mask = SupervisorActionMask.Render(context);

        mask.ShouldNotBeNull();
        mask!.ShouldContain("resolve cap is spent (1 of 1)", Case.Insensitive);
        mask.ShouldContain("FORCE-STOPS this run", Case.Sensitive, "the consequence differs from every other cap — a wave is refused, this ends the run");
        mask.ShouldContain("ask one to rule", Case.Insensitive, "the honest exits are named alongside the refusal");
    }

    [Fact]
    public void Under_the_cap_resolve_stays_available()
    {
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome()), Decision(2, SupervisorDecisionKinds.Resolve)) with { MaxResolveAttempts = 2 };

        SupervisorActionMask.Render(context).ShouldBeNull("one attempt of a two-attempt cap leaves a real move on the table");
    }

    [Fact]
    public void A_legacy_context_without_a_cap_falls_back_to_the_lane_default()
    {
        var spent = Enumerable.Range(1, SupervisorLane.DefaultMaxResolveAttempts)
            .Select(i => Decision(i + 1, SupervisorDecisionKinds.Resolve))
            .Prepend(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome()))
            .ToArray();

        SupervisorActionMask.Render(Context(spent))!.ShouldContain("resolve cap is spent", Case.Insensitive);
    }

    // ── The anti-drift pin: the mask and the executor read ONE conflict authority ────

    [Theory]
    [InlineData(SupervisorDecisionKinds.Merge)]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    public void The_mask_and_the_resolve_executor_agree_on_conflict_presence(string kind)
    {
        var conflicted = Context(Decision(1, kind, ConflictedOutcome()));
        var clean = Context(Decision(1, kind));

        // Same tape, same answer — two implementations of "is there a conflict" would drift into two behaviours.
        RealSupervisorActionExecutor.FindMostRecentConflictDecision(conflicted).ShouldNotBeNull();
        SupervisorActionMask.ResolveUnavailableReason(conflicted).ShouldBeNull();

        RealSupervisorActionExecutor.FindMostRecentConflictDecision(clean).ShouldBeNull();
        SupervisorActionMask.ResolveUnavailableReason(clean).ShouldNotBeNull();
    }

    // ── The never-mask floor ─────────────────────────────────────────────────────────

    [Fact]
    public void The_mask_never_names_an_escape_hatch_or_a_judgement_call_verb()
    {
        // plan / ask_human / stop are the way out of every dead end; merge and spawn/retry futility is a judgement,
        // not a structural fact (the merge set excludes a resolve's own agent run, so "nothing folded" reads futile
        // exactly where merging a VERIFIED resolution is correct).
        var mask = SupervisorActionMask.Render(Context(Decision(1, SupervisorDecisionKinds.Plan)))!;

        foreach (var verb in new[] { "plan", "ask_human", "stop", "merge", "spawn", "retry" })
            mask.ShouldNotContain($"- {verb} —", Case.Sensitive, $"{verb} must never be masked");
    }

    [Fact]
    public void The_header_is_pinned()
    {
        SupervisorActionMask.Header.ShouldBe("UNAVAILABLE THIS TURN (choosing one of these cannot advance the run):");
    }

    // ── The prompt wiring ────────────────────────────────────────────────────────────

    [Fact]
    public void The_user_prompt_carries_the_mask_and_omits_it_when_everything_is_available()
    {
        var masked = LlmSupervisorDecider.BuildUserPromptForTest(Context(Decision(1, SupervisorDecisionKinds.Plan)));
        masked.ShouldContain("UNAVAILABLE THIS TURN", Case.Sensitive);

        var available = LlmSupervisorDecider.BuildUserPromptForTest(Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome())));
        available.ShouldNotContain("UNAVAILABLE THIS TURN", Case.Sensitive);
    }
}
