using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins A1.5's action mask — the prompt block naming what CANNOT advance the run this turn. It covers the
/// two verbs whose availability is a server-decided fact rather than a judgement: <c>resolve</c> (with no recorded
/// conflict the executor no-ops it — a wasted turn — and past the resolve cap the bounds FORCE-STOP the whole run)
/// and <c>amend_acceptance</c> (with no unit whose latest check is infra-classed, <see cref="SupervisorAmendPrecondition"/>
/// refuses the proposal synchronously — no card, no human, the turn gone). Also pins the two properties that make
/// a mask safe: it renders NOTHING when everything is available, and each arm reads the SAME authority the executor
/// acts on, so the mask and the refusal can never disagree.
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

    private static string CleanOutcome() => JsonSerializer.Serialize(new
    {
        integration = new { status = "Clean", integratedBranch = "codespace/integration/run", outcomes = Array.Empty<object>() },
    }, AgentJson.Options);

    private static string StagedOutcome() => JsonSerializer.Serialize(new { agentRunIds = new[] { Guid.NewGuid() }, agentCount = 1 }, AgentJson.Options);

    /// <summary>A resolve whose resolver agent terminated, folded by the PRODUCTION folder so the verdict is read off bytes the server really writes — <paramref name="verified"/> carries the resolver recipe's own tested marker, which is the only thing that makes <c>SupervisorOutcome.ReadResolutionVerdict</c> say Verified.</summary>
    private static SupervisorPriorDecision ResolveDecision(long sequence, bool verified)
    {
        var resolver = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(),
            Status = "Succeeded",
            ProducedBranch = "codespace/resolve/head",
            Summary = verified ? $"reconciled the conflict; build and tests pass {SupervisorResolverRecipe.TestsPassedMarker}" : "attempted to reconcile, but the build still fails",
        };

        var staged = JsonSerializer.Serialize(new { agentRunIds = new[] { resolver.AgentRunId }, agentCount = 1 }, AgentJson.Options);

        return Decision(sequence, SupervisorDecisionKinds.Resolve, SupervisorOutcome.FoldAgentResults(staged, new[] { resolver }));
    }

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) =>
        new() { Goal = "ship it", TurnNumber = prior.Length, PriorDecisions = prior };

    /// <summary>A spawn whose one unit's check COULD NOT RUN — the only shape that leaves <c>amend_acceptance</c> genuinely available. Folded by the production folder, so a fixture the server could not produce cannot make this suite green.</summary>
    private static SupervisorPriorDecision AmendableSpawn(long sequence = 1, string detail = "grade-error: npm: command not found")
    {
        var unit = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "codespace/agent/s1", AcceptancePassed = false, AcceptanceDetail = detail };
        var outcome = SupervisorOutcome.FoldAgentResults(JsonSerializer.Serialize(new { agentRunIds = new[] { unit.AgentRunId }, agentCount = 1 }, AgentJson.Options), new[] { unit });

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskIds = new[] { "s1" } }, AgentJson.Options), OutcomeJson = outcome,
        };
    }

    /// <summary>The ONLY tape shape on which nothing at all is masked: a live conflict with resolve budget left AND a unit whose check could not run.</summary>
    private static SupervisorTurnContext NothingMasked() =>
        Context(AmendableSpawn(), Decision(2, SupervisorDecisionKinds.Merge, ConflictedOutcome())) with { MaxResolveAttempts = 2 };

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

    [Fact]
    public void A_clean_merge_with_no_later_staged_work_masks_a_remerge_that_cannot_advance()
    {
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, CleanOutcome()));

        SupervisorActionMask.MergeUnavailableReason(context).ShouldNotBeNull();
        SupervisorActionMask.Render(context)!.ShouldContain("- merge — ", Case.Sensitive);
    }

    [Fact]
    public void Planning_alone_after_a_clean_merge_does_not_invent_new_work_to_merge()
    {
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, CleanOutcome()), Decision(2, SupervisorDecisionKinds.Plan));

        SupervisorActionMask.MergeUnavailableReason(context).ShouldNotBeNull();
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    public void Real_staged_work_after_a_clean_merge_makes_merge_available_again(string kind)
    {
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, CleanOutcome()), Decision(2, kind, StagedOutcome()));

        SupervisorActionMask.MergeUnavailableReason(context).ShouldBeNull("new agent work must still be folded");
    }

    [Fact]
    public void A_verified_resolution_is_the_one_reconciliation_a_merge_may_accept()
    {
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome()), ResolveDecision(2, verified: true));

        SupervisorActionMask.MergeUnavailableReason(context).ShouldBeNull("a VERIFIED resolution is exactly the state whose merge surfaces the resolver's tested branch — the required merge this mask has always preserved");
    }

    // ── The unaccepted-reconciliation arm: the live miss on golden `resolve-cap-spent` ─────────────

    [Theory]
    [InlineData(false)]   // the resolver terminated without the verified marker — Unverified
    [InlineData(null)]    // nothing folded from the resolver at all — Unknown
    public void A_reconciliation_the_tape_never_accepted_does_not_re_open_merge(bool? folded)
    {
        // The executor accepts a resolution ONLY on Verified (SupervisorOutcome.ResolvedBranch → AcceptedResolutionBranch),
        // so a merge here does not surface the resolver's branch — it re-runs the integrator over the branches that
        // already conflicted. Both wires answered 'merge' on exactly this tape (run 34940616446, 28/29 each) while
        // the menu still offered the verb.
        var resolve = folded is null ? Decision(2, SupervisorDecisionKinds.Resolve, StagedOutcome()) : ResolveDecision(2, folded.Value);
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome()), resolve);

        SupervisorActionMask.MergeUnavailableReason(context).ShouldBe(SupervisorActionMask.UnacceptedReconciliation);
        SupervisorActionRoster.Offerable(context).ShouldNotContain(SupervisorDecisionKinds.Merge, "a verb the mask withholds must never be on the menu above it");
        SupervisorActionRoster.Withheld(context).ShouldContain(SupervisorDecisionKinds.Merge);
    }

    [Theory]
    [InlineData(SupervisorDecisionStatus.Running)]
    [InlineData(SupervisorDecisionStatus.Failed)]
    public void A_resolve_that_is_still_running_or_failed_outright_withholds_merge_too(SupervisorDecisionStatus status)
    {
        // The arm asks the EXECUTOR's question — the last staging decision in the plan window, with no status or
        // staged-count filter of its own (RealSupervisorActionExecutor.AcceptedResolutionBranch). A reverse walk
        // filtered on Succeeded would skip both of these, land on the older spawn, and offer a merge mid-reconciliation.
        var resolve = ResolveDecision(2, verified: false) with { Status = status };
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome()), resolve);

        SupervisorActionMask.MergeUnavailableReason(context).ShouldBe(SupervisorActionMask.UnacceptedReconciliation);
    }

    [Fact]
    public void A_later_staging_decision_displaces_the_resolution_question_exactly_as_the_executor_does()
    {
        // Mask and executor now select the same "newest staging" decision, so they agree that this tape's merge is
        // NOT about a reconciliation. What the executor then does with it — re-running the integrator instead of
        // surfacing the earlier VERIFIED resolution a failed spawn displaced — is an executor-side gap that predates
        // this arm (RealSupervisorActionExecutor.Integrate.cs, AcceptedResolutionBranch); the mask's job is to stop
        // describing a different merge than the one that would run.
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome()), ResolveDecision(2, verified: true), Decision(3, SupervisorDecisionKinds.Spawn));

        SupervisorActionMask.MergeUnavailableReason(context).ShouldBeNull();
    }

    [Fact]
    public void A_rejected_staging_decision_after_a_clean_merge_does_not_make_the_old_work_mergeable_again()
    {
        var context = Context(Decision(1, SupervisorDecisionKinds.Merge, CleanOutcome()), Decision(2, SupervisorDecisionKinds.Retry));

        SupervisorActionMask.MergeUnavailableReason(context).ShouldNotBeNull("a staging verb that produced zero agent runs created no work frontier");
    }

    // ── The available arm: nothing is masked, so nothing renders ─────────────────────

    [Fact]
    public void With_every_covered_verb_available_the_block_renders_nothing()
    {
        // The byte-identity property: an available action set must not add a block. Asserted on the one tape shape
        // that really leaves both covered verbs reachable — a live conflict with budget AND a unit whose check could
        // not run. Weakening it to a conflict alone would let the amend arm regress to "always masked" unnoticed.
        SupervisorActionMask.Render(NothingMasked())
            .ShouldBeNull("both covered verbs are genuinely available here — masking either would steer the model away from a right move");
    }

    [Fact]
    public void A_spawn_staging_conflict_also_makes_resolve_available()
    {
        // The widened source: a spawn whose dependency staging could not auto-integrate records the SAME integration
        // shape a merge does. Missing that would mask the one verb that reconciles it.
        SupervisorActionMask.ResolveUnavailableReason(Context(Decision(1, SupervisorDecisionKinds.Spawn, ConflictedOutcome())))
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

        SupervisorActionMask.ResolveUnavailableReason(context).ShouldBeNull("one attempt of a two-attempt cap leaves a real move on the table");
    }

    // ── The amend arm: an amend the server would refuse before any human sees it ─────

    [Fact]
    public void With_no_infra_failed_unit_amend_acceptance_is_masked()
    {
        var mask = SupervisorActionMask.Render(Context(Decision(1, SupervisorDecisionKinds.Plan)))!;

        mask.ShouldContain("- amend_acceptance — ", Case.Sensitive, "nothing on this tape is amendable, so the proposal is refused synchronously and the turn is spent");
        mask.ShouldContain("no unit has an infra-failed check to amend", Case.Sensitive);
        mask.ShouldContain("RECORDED verdict", Case.Sensitive, "the reason must say the SERVER rules on the evidence — a model that thinks its framing decides will keep proposing");
    }

    [Fact]
    public void A_unit_whose_check_could_not_run_makes_amend_acceptance_available()
    {
        SupervisorActionMask.AmendUnavailableReason(Context(AmendableSpawn()))
            .ShouldBeNull("the check itself could not run — exactly the broken-oracle evidence the amend verb exists for");
    }

    [Fact]
    public void A_check_that_ran_and_rejected_the_work_does_not_make_amend_available()
    {
        // The mark-its-own-homework channel: offering the verb here invites the model to amend the judge away, and
        // the server would refuse it anyway.
        SupervisorActionMask.AmendUnavailableReason(Context(AmendableSpawn(detail: "tests-failed-exit-1")))
            .ShouldNotBeNull("a check that RAN and rejected the work is evidence against the WORK, not the check");
    }

    [Theory]
    [InlineData("grade-error: npm: command not found")]
    [InlineData("tests-failed-exit-1")]
    public void The_mask_and_the_amend_precondition_agree_on_which_units_are_amendable(string detail)
    {
        // Same tape, same answer — two implementations of "is anything amendable" would drift into a menu that
        // offers the verb the executor refuses, which is the whole shape this block exists to prevent.
        var context = Context(AmendableSpawn(detail: detail));
        var amendable = SupervisorAmendPrecondition.Reject(context, new SupervisorAmendAcceptancePayload { SubtaskId = "s1", Waive = true, Reason = "r" }) is null;

        (SupervisorActionMask.AmendUnavailableReason(context) is null).ShouldBe(amendable);
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
    public void The_mask_never_names_an_escape_hatch_or_an_agent_staging_verb()
    {
        // plan / ask_human / stop are the way out of every dead end. Agent-staging verbs remain model judgements;
        // merge is masked only from the durable clean-integration frontier, never from a guessed empty fold.
        var mask = SupervisorActionMask.Render(Context(Decision(1, SupervisorDecisionKinds.Plan)))!;

        foreach (var verb in new[] { "plan", "ask_human", "stop", "spawn", "retry" })
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

        LlmSupervisorDecider.BuildUserPromptForTest(NothingMasked())
            .ShouldNotContain("UNAVAILABLE THIS TURN", Case.Sensitive);
    }
}
