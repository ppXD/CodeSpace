using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: pins the turn's VERB ROSTER — the block the model chooses its <c>kind</c> from — as a rendering of
/// <see cref="SupervisorActionMask"/> rather than a static list beside it.
///
/// <para>The defect these pin is prompt INCOHERENCE, not a wrong mask. The mask has named the unavailable verb
/// since A1.5; the roster listing all seven verbs regardless sat in the turn-invariant system prompt, so a masked
/// verb was still presented as choosable in the same prompt that forbade it. Golden <c>resolve-cap-spent</c> — a
/// conflicted merge whose resolve budget is spent, accepted answers {stop, ask_human} — passed four main runs in a
/// row on the gating Anthropic wire and then failed 2 of 4 branch lanes, once answering <c>resolve</c> (the masked
/// verb) and once <c>merge</c> (the merge that had already conflicted). #1795's rule was "never name a masked verb
/// in the steer"; these generalise it to the roster the steer sits under.</para>
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorActionRosterTests
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

    /// <summary>A tape with a conflicted merge and no resolve yet — resolve is genuinely available.</summary>
    private static SupervisorTurnContext ConflictWithBudget() =>
        Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome())) with { MaxResolveAttempts = 2 };

    /// <summary>The golden <c>resolve-cap-spent</c> shape: a conflicted merge, one resolve spent, a cap of one.</summary>
    private static SupervisorTurnContext ConflictWithCapSpent() =>
        Context(Decision(1, SupervisorDecisionKinds.Merge, ConflictedOutcome()), Decision(2, SupervisorDecisionKinds.Resolve)) with { MaxResolveAttempts = 1 };

    /// <summary>A spawn whose one unit's check COULD NOT RUN — the only shape that leaves <c>amend_acceptance</c> genuinely available. Folded by the production folder, so a fixture the server could not produce cannot make this suite green.</summary>
    private static SupervisorPriorDecision AmendableSpawn(long sequence = 1)
    {
        var unit = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "codespace/agent/s1", AcceptancePassed = false, AcceptanceDetail = "grade-error: npm: command not found" };
        var outcome = SupervisorOutcome.FoldAgentResults(JsonSerializer.Serialize(new { agentRunIds = new[] { unit.AgentRunId }, agentCount = 1 }, AgentJson.Options), new[] { unit });

        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskIds = new[] { "s1" } }, AgentJson.Options), OutcomeJson = outcome,
        };
    }

    /// <summary>The ONLY tape shape that withholds nothing: a live conflict with resolve budget left AND a unit whose check could not run.</summary>
    private static SupervisorTurnContext NothingWithheld() =>
        Context(AmendableSpawn(), Decision(2, SupervisorDecisionKinds.Merge, ConflictedOutcome())) with { MaxResolveAttempts = 2 };

    /// <summary>The offered verbs as the RENDERED block actually presents them — read back off the text, so a verb the roster computes as offerable but never prints (or prints without offering) is caught.</summary>
    private static IReadOnlyList<string> RenderedOffers(string block) => block
        .Split('\n')
        .TakeWhile(line => !line.StartsWith(SupervisorActionMask.Header, StringComparison.Ordinal))
        .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
        .Select(line => line[2..line.IndexOf(" — ", StringComparison.Ordinal)])
        .ToList();

    // ── (a) The cap-spent arm: the verb the mask forbids is not on the menu ──────────

    [Fact]
    public void With_the_resolve_cap_spent_the_roster_stops_offering_resolve_and_says_why()
    {
        var block = SupervisorActionRoster.Render(ConflictWithCapSpent());

        RenderedOffers(block).ShouldNotContain(SupervisorDecisionKinds.Resolve,
            "the mask forbids resolve on this tape — offering it in the same block is the contradiction golden 'resolve-cap-spent' answered with 'resolve'");
        block.ShouldContain(SupervisorActionMask.Header, Case.Sensitive, "…and the verb must still be NAMED as withheld, with the reason");
        block.ShouldContain("resolve cap is spent (1 of 1)", Case.Insensitive, "the reason is the cap, not an absent conflict — the two arms steer differently");
        block.ShouldContain("FORCE-STOPS this run", Case.Sensitive, "a spent resolve cap does not refuse the verb, it ends the run");

        // The withheld half is the MASK's own output, byte for byte — asserted, not left true by construction. A
        // roster that starts re-rendering the reasons itself is a second authority on availability, which is the
        // drift the whole block exists to prevent.
        block.ShouldEndWith(SupervisorActionMask.Render(ConflictWithCapSpent())!);
    }

    [Fact]
    public void The_conflicted_integration_block_stops_inviting_resolve_and_a_re_merge_once_the_cap_is_spent()
    {
        // The closest, loudest copy on the tape, and the one that named BOTH verbs the live miss answered with:
        // "To reconcile: choose 'resolve' … then you merge again." A model picks its verb off this line (M0).
        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(ConflictWithCapSpent());

        prompt.ShouldContain("INTEGRATION CONFLICTED", Case.Sensitive, "the conflict itself is still reported — only the moves it offers change");
        prompt.ShouldNotContain("To reconcile: choose 'resolve'", Case.Sensitive, "the block invites the verb the mask below it forbids");
        prompt.ShouldNotContain("then you merge again", Case.Sensitive, "…and re-invites the merge that already conflicted, which is the other answer the gating wire gave");
        prompt.ShouldContain("'resolve' is NOT available on this run any more", Case.Sensitive, "the fact replaces the invitation, so the model is not left guessing why the verb vanished");
    }

    [Fact]
    public void The_prompts_closing_sentence_stops_asking_for_a_merge_once_the_cap_is_spent()
    {
        // The last sentence sits in the recency slot right under the roster, and it was unconditional: "…then merge
        // the successful results, then stop." On a cap-spent conflicted tape that is a standing instruction to re-run
        // the merge already recorded as conflicted — the OTHER answer the gating wire gave on golden
        // 'resolve-cap-spent', and the one the roster alone does not withdraw.
        var capSpent = LlmSupervisorDecider.BuildUserPromptForTest(ConflictWithCapSpent());

        capSpent.TrimEnd().ShouldEndWith($"{LlmSupervisorDecider.ClosingCannotLand} Return ONLY the schema-constrained JSON.",
            Case.Sensitive, "the last thing the model reads must not be an instruction to merge again");
        capSpent.ShouldNotContain(LlmSupervisorDecider.ClosingLandsWithAMerge, Case.Sensitive, "no landing is reachable on this tape — offering one is the live miss with a new name");
    }

    [Theory]
    [InlineData("no-conflict")]
    [InlineData("conflict-with-budget")]
    public void Every_other_tape_keeps_the_closing_sentence_byte_identical(string shape)
    {
        // The negative control: the cap-aware arm is a substitution on ONE tape shape, not a rewrite of the rails.
        // A conflict with budget left still lands with a merge — after the resolve the block above it offers.
        var context = shape == "conflict-with-budget" ? ConflictWithBudget() : Context(Decision(1, SupervisorDecisionKinds.Plan));

        LlmSupervisorDecider.BuildUserPromptForTest(context).TrimEnd()
            .ShouldEndWith($"{LlmSupervisorDecider.ClosingLandsWithAMerge} Return ONLY the schema-constrained JSON.", Case.Sensitive);
    }

    [Fact]
    public void The_summarizer_fold_reads_the_same_spent_cap_the_live_render_does()
    {
        // The fold's output is PERSISTED and re-enters the live prompt as the rolling digest on every later turn, so
        // a cap-BLIND fold is a back door: the retired invitation reaches the model through the one block the
        // cap-aware renderer never gets to look at again. Same mask, same answer, both directions.
        var capSpent = ConflictWithCapSpent();
        var folded = LlmSupervisorDecider.BuildSummarizerPromptForTest(capSpent, capSpent.PriorDecisions);

        folded.ShouldContain("INTEGRATION CONFLICTED", Case.Sensitive, "the fold still reports the conflict — only the moves it offers change");
        folded.ShouldNotContain("To reconcile: choose 'resolve'", Case.Sensitive, "the digest must not carry an invitation to a verb that would FORCE-STOP the run");
        folded.ShouldNotContain("then you merge again", Case.Sensitive, "…nor to the merge that already conflicted");
        folded.ShouldContain(LlmSupervisorDecider.ResolveWithdrawnOnAConflictedIntegration, Case.Sensitive, "the fact replaces the invitation, exactly as the live render does");

        var withBudget = ConflictWithBudget();

        LlmSupervisorDecider.BuildSummarizerPromptForTest(withBudget, withBudget.PriorDecisions)
            .ShouldContain("To reconcile: choose 'resolve'", Case.Sensitive, "the negative control — a fold with budget left must keep the move the run can still make");
    }

    [Fact]
    public void With_budget_left_the_conflicted_integration_block_still_names_the_resolve_move()
    {
        // The negative control for the arm above: the cap-aware line must not swallow the guidance that makes the
        // four resolve-graded goldens answerable.
        LlmSupervisorDecider.BuildUserPromptForTest(ConflictWithBudget())
            .ShouldContain("To reconcile: choose 'resolve'", Case.Sensitive, "resolve is genuinely available here — withholding the steer would break the move the corpus grades");
    }

    // ── (b) The available arm: a live conflict with budget offers resolve ────────────

    [Fact]
    public void With_a_conflict_and_budget_left_resolve_is_offered()
    {
        RenderedOffers(SupervisorActionRoster.Render(ConflictWithBudget())).ShouldContain(SupervisorDecisionKinds.Resolve);
    }

    [Fact]
    public void With_every_verb_reachable_the_withheld_half_does_not_render_at_all()
    {
        var block = SupervisorActionRoster.Render(NothingWithheld());

        RenderedOffers(block).ShouldBe(SchemaKinds(), ignoreOrder: true, "every schema verb is reachable on this tape, so the menu must offer all eight");
        block.ShouldNotContain(SupervisorActionMask.Header, Case.Sensitive, "nothing is masked on this tape, so the withheld half must not render at all");
    }

    [Fact]
    public void With_no_amendable_unit_amend_acceptance_is_withheld_as_a_refusal()
    {
        var block = SupervisorActionRoster.Render(ConflictWithBudget());

        RenderedOffers(block).ShouldNotContain(SupervisorDecisionKinds.AmendAcceptance,
            "nothing on this tape is amendable — the server refuses the proposal before any human sees it, so offering the verb spends the turn for nothing");
        block.ShouldContain("no unit has an infra-failed check to amend", Case.Sensitive, "…and the verb is still NAMED as withheld, with the reason");
    }

    // ── (c) The no-conflict arm: resolve would no-op ─────────────────────────────────

    [Fact]
    public void With_no_conflict_recorded_resolve_is_withheld_as_a_no_op()
    {
        var block = SupervisorActionRoster.Render(Context(Decision(1, SupervisorDecisionKinds.Plan)));

        RenderedOffers(block).ShouldNotContain(SupervisorDecisionKinds.Resolve);
        block.ShouldContain("nothing to reconcile", Case.Insensitive, "the no-conflict arm reads differently from the cap arm — one wastes a turn, the other ends the run");
    }

    // ── (d) Coherence: one reader answers both halves ────────────────────────────────

    [Theory]
    [InlineData("no-conflict")]
    [InlineData("conflict-with-budget")]
    [InlineData("conflict-cap-spent")]
    [InlineData("nothing-withheld")]
    public void The_rostered_verbs_are_exactly_the_verbs_the_mask_leaves_available(string shape)
    {
        var context = shape switch
        {
            "conflict-with-budget" => ConflictWithBudget(),
            "conflict-cap-spent" => ConflictWithCapSpent(),
            "nothing-withheld" => NothingWithheld(),
            _ => Context(Decision(1, SupervisorDecisionKinds.Plan)),
        };

        var offerable = SupervisorActionRoster.Offerable(context);
        var withheld = SupervisorActionRoster.Withheld(context);

        // Derived from the mask's own per-verb reader, never restated: a second opinion here is the drift this block
        // exists to prevent — the roster would offer a verb the mask three lines below withholds.
        withheld.ShouldBe(SchemaKinds().Where(verb => SupervisorActionMask.UnavailableReasonFor(verb, context) is not null).ToList(), ignoreOrder: true,
            "the mask is the only reader of which verbs a server verdict has taken off the table");

        offerable.Intersect(withheld, StringComparer.Ordinal).ShouldBeEmpty("a verb cannot be offered and withheld in the same breath");
        offerable.Concat(withheld).ToList().ShouldBe(SchemaKinds(), ignoreOrder: true,
            "the two halves must PARTITION the schema's vocabulary — a verb in neither is a verb the model is never told exists");

        RenderedOffers(SupervisorActionRoster.Render(context)).ShouldBe(offerable,
            "the rendered menu must be the computed menu — a roster whose text and whose reader disagree is the original defect with an extra step");
    }

    [Fact]
    public void The_escape_hatches_are_offered_on_every_tape()
    {
        // plan / ask_human / stop are the way out of every dead end; the mask may never take them away
        // (SupervisorActionMask's own never-mask floor), so no tape may render a roster without them.
        foreach (var context in new[] { Context(), Context(Decision(1, SupervisorDecisionKinds.Plan)), ConflictWithBudget(), ConflictWithCapSpent() })
        foreach (var hatch in new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.AskHuman, SupervisorDecisionKinds.Stop })
            RenderedOffers(SupervisorActionRoster.Render(context)).ShouldContain(hatch, $"'{hatch}' is an escape hatch — a turn that offers none is a dead end");
    }

    /// <summary>
    /// The header promises "the decision 'kind' values this turn accepts", so the roster's vocabulary must BE the
    /// schema's <c>kind</c> enum — read off the wire contract rather than restated, or the two drift apart again.
    /// A verb the schema binds but the menu never names reads to the model as a verb that does not exist: the same
    /// "two rosters" defect one level up, and golden <c>amended-oracle-discarded-by-replan</c> is graded on exactly
    /// such a verb (<c>amend_acceptance</c>) while the nearest block claiming exhaustiveness omitted it.
    /// </summary>
    [Fact]
    public void The_roster_vocabulary_is_exactly_the_schema_kind_enum()
    {
        var context = ConflictWithBudget();
        var vocabulary = SupervisorActionRoster.Offerable(context).Concat(SupervisorActionRoster.Withheld(context)).ToList();

        vocabulary.ShouldBe(SchemaKinds(), ignoreOrder: true,
            "the block claims to name every 'kind' this turn accepts — a verb only one of the two knows about is a verb the model is either never offered or offered and then refused");
    }

    /// <summary>The schema's own <c>kind</c> enum, read off the wire contract — never a second list this file could drift from.</summary>
    private static IReadOnlyList<string> SchemaKinds() =>
        SupervisorDecisionSchema.ResponseSchema.GetProperty("properties").GetProperty("kind").GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()!).ToList();

    [Fact]
    public void The_header_is_pinned()
    {
        SupervisorActionRoster.Header.ShouldBe("ACTIONS AVAILABLE THIS TURN (the decision 'kind' values this turn accepts — choose exactly one):");
    }

    // ── The prompt wiring ────────────────────────────────────────────────────────────

    [Fact]
    public void The_user_prompt_carries_the_roster_on_every_turn_and_the_system_prompt_defers_to_it()
    {
        // The roster is per-TURN now, so it can only live where the turn's state lives. The system prompt keeps a
        // pointer that names no verb — a sentence no turn can contradict.
        LlmSupervisorDecider.BuildUserPromptForTest(Context()).ShouldContain(SupervisorActionRoster.Header, Case.Sensitive);
        LlmSupervisorDecider.BuildUserPromptForTest(ConflictWithBudget()).ShouldContain(SupervisorActionRoster.Header, Case.Sensitive);

        var system = LlmSupervisorDecider.SystemPromptForTest;

        system.ShouldContain(SupervisorActionRoster.SystemPromptPointer, Case.Sensitive, "the rails must tell the model which block is authoritative about what it may emit");
        system.ShouldNotContain("fixed vocabulary", Case.Insensitive, "the static seven-verb roster is GONE — co-presence would leave the masked verb presented as choosable, which is the whole defect");
    }
}
