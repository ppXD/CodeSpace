using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Room;

/// <summary>
/// A1 (result honesty) — the RESULT card's verdict. The Session Room used to read ONLY the stop's own classification,
/// so a run whose objective acceptance check FAILED still painted the green "Result" behind the model's own
/// success-sounding closing line (the engine's honest word lives on the run row, never on the card). These pin the
/// composition against the SAME authority the run row's <c>Outcome</c> is folded through, so the two cannot drift.
///
/// <para>Tier: Unit — <see cref="RoomProjector.ResultVerdict"/> is pure over the two durable stop-decision facts.</para>
/// </summary>
[Trait("Category", "Unit")]
public class RoomResultVerdictTests
{
    [Fact]
    public void A_stop_whose_acceptance_grade_FAILED_degrades_the_card_and_names_the_reason()
    {
        // The model stopped orderly and wrote a confident closing line; the objective check disagreed.
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: false, Stop(SupervisorStopKind.Succeeded, summary: "Fixed the flaky tests."));

        verdict.Degraded.ShouldBeTrue("the work missed its own definition of done — a green Result would be a lie");
        verdict.Reason.ShouldBe("Checks failed", "the card states the ledger's verdict itself, because the answer TEXT is the model's success claim");
    }

    [Fact]
    public void A_stop_whose_acceptance_grade_PASSED_keeps_the_green_result()
    {
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: true, Stop(SupervisorStopKind.Succeeded, summary: "Fixed the flaky tests."));

        verdict.Degraded.ShouldBeFalse();
        verdict.Reason.ShouldBeNull();
    }

    [Fact]
    public void An_UNGRADED_succeeded_stop_is_untouched_by_the_acceptance_axis()
    {
        // A run that staked no oracle at all: absence is not a verdict. Byte-identical to the pre-A1 projection.
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: null, Stop(SupervisorStopKind.Succeeded, summary: "Answered inline."));

        verdict.Degraded.ShouldBeFalse("a null grade must never manufacture a degrade");
        verdict.Reason.ShouldBeNull();
    }

    [Theory]
    [InlineData(SupervisorStopKind.GaveUp)]
    [InlineData(SupervisorStopKind.Forced)]
    [InlineData(SupervisorStopKind.NeedsClarification)]
    public void An_UNGRADED_non_success_stop_stays_degraded_with_NO_reason_line(SupervisorStopKind kind)
    {
        // Regression guard: the classifier's own shapes are unchanged, and they need no extra reason line — the card's
        // TEXT already IS the classifier's account of why the run stopped short.
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: null, Stop(kind, reason: "no progress"));

        verdict.Degraded.ShouldBeTrue();
        verdict.Reason.ShouldBeNull("a second, redundant account would only repeat the card's own text");
    }

    [Fact]
    public void A_FAILED_grade_outranks_the_stop_classification()
    {
        // Both axes fire. The grade is the objective one, so it owns the reason line.
        var verdict = RoomProjector.ResultVerdict(acceptancePassed: false, Stop(SupervisorStopKind.Forced, reason: "cost cap reached"));

        verdict.Degraded.ShouldBeTrue();
        verdict.Reason.ShouldBe("Checks failed");
    }

    /// <summary>
    /// The COUPLING pin: drive the whole stop-kind × grade cross-product from the RAW payload / outcome bytes a stop
    /// decision actually persists, and hold the card's verdict against <see cref="SupervisorOutcome.HonestOutcome"/> —
    /// the very function the engine folds into <c>WorkflowRun.Outcome</c>. If the two ever disagree, the room is lying
    /// about a run the ledger already judged.
    ///
    /// <para>Honest about its reach: today a naive <c>acceptancePassed is false</c> body ALSO satisfies this, because
    /// <c>HonestOutcomeOf</c> is extensionally equal to it right now — so this theory cannot, at present, tell the two
    /// apart (verified by mutation). What it does buy is the RELATIONSHIP: it goes red the moment the card's rule and
    /// the engine's rule diverge — a new degraded word, a widened success set, a waived-grade change on either side,
    /// or a card that starts reading absence as a verdict (that last one turns 4 of these cases red today).</para>
    /// </summary>
    [Theory]
    [InlineData("completed", null, null)]
    [InlineData("completed", null, true)]
    [InlineData("completed", null, false)]
    [InlineData("no-decision", null, null)]
    [InlineData("no-decision", null, true)]
    [InlineData("no-decision", null, false)]
    [InlineData("needs_clarification", null, null)]
    [InlineData("needs_clarification", null, true)]
    [InlineData("needs_clarification", null, false)]
    [InlineData(null, "no progress", null)]
    [InlineData(null, "no progress", true)]
    [InlineData(null, "no progress", false)]
    public void The_cards_verdict_never_disagrees_with_the_engines_own_honest_outcome(string? outcome, string? forcedReason, bool? grade)
    {
        var payloadJson = forcedReason is null ? "{}" : JsonSerializer.Serialize(new { reason = forcedReason });
        var outcomeJson = JsonSerializer.Serialize(new { stopped = true, outcome, summary = "The supervisor's closing line." });

        if (grade is { } passed)
            outcomeJson = SupervisorOutcome.AppendAcceptanceGrade(outcomeJson, passed, detail: "2 of 7 tests failed");

        // Read the two facts back off the bytes exactly as the projector does.
        var acceptancePassed = SupervisorOutcome.ReadAcceptanceGradePassed(outcomeJson);
        var stopClass = SupervisorOutcome.ClassifyStop(payloadJson, outcomeJson);

        acceptancePassed.ShouldBe(grade, "the fixture must round-trip through the real fold/read pair, or this theory proves nothing");

        var honestOutcome = SupervisorOutcome.HonestOutcome(payloadJson, outcomeJson);
        var acceptanceFailed = honestOutcome == SupervisorOutcome.AcceptanceFailedOutcome;

        var verdict = RoomProjector.ResultVerdict(acceptancePassed, stopClass);

        verdict.Degraded.ShouldBe(acceptanceFailed || stopClass.Degraded,
            customMessage: $"the card and the run row must agree: outcome={outcome ?? "<null>"} reason={forcedReason ?? "<null>"} grade={grade?.ToString() ?? "<null>"} → honest word '{honestOutcome}'");

        (verdict.Reason is not null).ShouldBe(acceptanceFailed,
            customMessage: $"a reason line is owed EXACTLY when the honest word is {SupervisorOutcome.AcceptanceFailedOutcome} (got '{honestOutcome}', reason '{verdict.Reason ?? "<null>"}')");
    }

    private static SupervisorStopClassification Stop(SupervisorStopKind kind, string? summary = null, string? reason = null) =>
        new() { Kind = kind, Summary = summary, Reason = reason };

    // ── C1: the UNVERIFIED marker ──

    [Fact]
    public void A_success_that_nothing_checked_is_marked_unverified()
    {
        // The most expensive silence in the room: a run with no operator floor, no model-authored oracle and no output
        // critic terminalizes a green "Result" that reads exactly like a fully-verified one.
        var verification = RoomProjector.Verification(graded: false, review: null);

        verification.Verified.ShouldBe(false);
        verification.Note.ShouldBe("Unverified — no check ran on this result", "the copy is BACKEND-authored — the FE never maps a flag to words");
    }

    [Theory]
    [InlineData(true, false)]    // an acceptance grade every graded unit PASSED (the stop's, or the per-unit fold's)
    [InlineData(false, true)]    // an output-critic verdict that APPROVED
    [InlineData(true, true)]
    public void A_success_something_checked_and_passed_carries_no_marker(bool graded, bool approvedReview)
    {
        var verification = RoomProjector.Verification(graded, approvedReview ? (true, "Looks correct.", null) : null);

        verification.Verified.ShouldBe(true);
        verification.Note.ShouldBeNull("a verified card is byte-identical to before — the chip exists only for the unexamined one");
    }

    [Fact]
    public void A_success_whose_output_review_FLAGGED_it_is_not_verified_and_carries_the_reviewers_reason()
    {
        // A flag is the STRONGEST evidence the room can hold that something examined this result — and it says the
        // opposite of verified. Counting "a critic.output call happened" as a pass spent the reviewer's objection as
        // its endorsement, and the objection itself (persisted as ReviewFeedback) reached no surface at all.
        var verification = RoomProjector.Verification(graded: false, review: (false, "The migration drops a column with no backfill.", null));

        verification.Verified.ShouldBe(false);
        verification.Note.ShouldBe("Unverified — the output review flagged this result: The migration drops a column with no backfill.",
            "the reviewer's own words reach the card — backend-framed, so the FE still never maps a flag to copy");
    }

    [Fact]
    public void A_flagged_review_with_no_words_still_says_which_silence_it_is()
    {
        RoomProjector.Verification(graded: false, review: (false, null, null)).Note
            .ShouldBe("Unverified — the output review flagged this result.", "an empty rationale must not degrade to the ungraded copy, which claims nothing looked");
    }

    // ── 5.6 residual: a review ATTEMPTED but never reaching a verdict (both rungs exhausted) ──

    [Fact]
    public void A_review_that_could_not_run_is_NOT_verified_and_carries_the_machines_own_reason()
    {
        // Both the S8 agent reviewer and the model-critic fallback exhausted without a verdict. This is neither an
        // endorsement nor an objection — the OLD fold read it exactly like "no review was ever configured", losing
        // the one thing this reader exists to keep: WHY nothing landed.
        var verification = RoomProjector.Verification(graded: false, review: (null, "No reviewer model is available in the team's pool.", null));

        verification.Verified.ShouldBe(false);
        verification.Note.ShouldBe("Unverified — the output review could not run: No reviewer model is available in the team's pool.",
            "the note says WHY, not just that nothing landed — the same treatment a flag already gets");
    }

    [Fact]
    public void An_unreviewed_result_with_no_reason_still_says_which_silence_it_is()
    {
        RoomProjector.Verification(graded: false, review: (null, null, null)).Note
            .ShouldBe("Unverified — the output review could not run.", "an empty reason must not degrade to the ungraded copy, which claims no review was ever attempted");
    }

    [Fact]
    public void A_GRADED_pass_outranks_an_unreviewed_result()
    {
        // Same precedence as a flag: an executed objective check outranks the review ladder's own silence.
        RoomProjector.Verification(graded: true, review: (null, "no reviewer model", null)).Verified.ShouldBe(true);
    }

    [Fact]
    public void The_unreviewed_branchs_name_reaches_the_chip_copy()
    {
        RoomProjector.Verification(graded: false, review: (null, "No reviewer model is available in the team's pool.", "parser")).Note
            .ShouldBe("Unverified — the output review could not run for parser: No reviewer model is available in the team's pool.");
    }

    [Fact]
    public void A_skipped_beat_reads_as_UNREVIEWED_not_as_a_flag_or_an_approval()
    {
        // The critic's own review.skipped payload never carries an "approved" key at all — a naive reader that
        // defaulted a missing key to false would silently relabel "never examined" as "examined and rejected".
        var verdict = RoomProjector.ReadReviewVerdict(SkippedVerdict("No reviewer model is available in the team's pool."));

        verdict.Approved.ShouldBeNull("attempted, never a verdict — not an approval and not a flag");
        verdict.Reason.ShouldBe("No reviewer model is available in the team's pool.");
    }

    [Fact]
    public void One_branchs_APPROVAL_can_no_longer_outrank_a_siblings_UNREVIEWED_silence()
    {
        // The same defect FoldReviewVerdicts already refuses for a flag, restated for the OTHER non-approved state: a
        // fan-out where one branch's review never ran must not have a sibling's approval paper over it.
        var fold = RoomProjector.FoldReviewVerdicts(
            new[]
            {
                (Cell("implement-parser", ""), 10L, SkippedVerdict("No reviewer model is available in the team's pool.")),
                (Cell("write-migration", ""), 11L, Verdict(Migration, approved: true, "Clean.")),
            },
            UnitLabels);

        fold.ShouldNotBeNull();
        fold!.Value.Approved.ShouldBeNull("one unit was never reviewed — the run cannot read as fully verified");
        fold.Value.Reason.ShouldBe("No reviewer model is available in the team's pool.");
        fold.Value.Unit.ShouldBe("parser");
    }

    [Fact]
    public void A_later_verdict_supersedes_an_earlier_skip_for_the_SAME_unit()
    {
        // Improve mode: the first critic call could not resolve a reviewer; a later round's call succeeded and
        // approved. The unit's LATEST word is the approval, so the earlier skip must not keep the run flagged as
        // unreviewed forever.
        var fold = RoomProjector.FoldReviewVerdicts(
            new[]
            {
                (Cell("agent", ""), 10L, SkippedVerdict("no reviewer model")),
                (Cell("agent", ""), 11L, Verdict(null, approved: true, "Clean on the second pass.")),
            },
            UnitLabels);

        fold.ShouldBe((true, (string?)null, (string?)null));
    }

    [Fact]
    public void A_GRADED_pass_outranks_a_flagged_review()
    {
        // The objective oracle ran and every unit it graded passed. The chip reports the strongest EVIDENCE, and a
        // model's opinion never outranks an executed check — the critic's own beat is only consulted when nothing ran.
        RoomProjector.Verification(graded: true, review: (false, "I would have done it differently.", null)).Verified.ShouldBe(true);
    }

    [Fact]
    public void A_success_whose_only_grade_read_the_stop_summary_is_marked_unverified_with_its_own_copy()
    {
        // A prose-judged pass IS a verdict — it decides Solved exactly as before — but the model's account of its own
        // work is not evidence about the work. Presenting it as "verified" would launder the weakest grade the system
        // can produce into the strongest claim the card can make.
        var verification = RoomProjector.Verification(graded: false, review: null, judgedSummary: true);

        verification.Verified.ShouldBe(false);
        verification.Note.ShouldBe("Unverified — judged from the stop summary", "the card says WHICH silence it is, not just that there is one");
    }

    [Fact]
    public void A_summary_judged_stop_alongside_a_real_check_is_verified()
    {
        // A unit grade or an output critic examined a real result; the prose grade riding alongside takes nothing away.
        RoomProjector.Verification(graded: true, review: null, judgedSummary: true).Verified.ShouldBe(true);
        RoomProjector.Verification(graded: false, review: (true, null, null), judgedSummary: true).Verified.ShouldBe(true);
    }

    [Fact]
    public void A_malformed_verdict_beat_reads_as_a_flag_not_as_an_approval()
    {
        // A half-written beat proves exactly one thing: a review ran. Reading its silence as approval is the same
        // over-claim in a smaller font.
        RoomProjector.ReadReviewVerdict("""{"kind":"critic.output"}""").Approved.ShouldBe(false);
        RoomProjector.ReadReviewVerdict("not json at all").Approved.ShouldBe(false);
        RoomProjector.ReadReviewVerdict("""{"kind":"critic.output","agentRunId":"a1","approved":true,"reason":"Fine."}""").ShouldBe((true, "Fine.", "a1"));
        RoomProjector.ReadReviewVerdict("""{"kind":"critic.output","approved":true}""").AgentRunId.ShouldBeNull("a beat that named no agent run falls back to its ledger cell, never to a bogus id");
    }

    // ── the per-CELL review fold: "did EVERY reviewed unit approve" ──

    [Fact]
    public void One_branchs_APPROVAL_can_no_longer_outrank_a_siblings_FLAG()
    {
        // The defect: a run is one ledger but a review is per UNIT — every fanned-out branch writes its own beat onto
        // the same run. Reading the run's single NEWEST beat therefore made write order the arbiter: the branch that
        // finished last decided the whole run's chip, so an approval landing after a sibling's rejection erased it.
        var fold = RoomProjector.FoldReviewVerdicts(
            new[]
            {
                (Cell("implement-parser", ""), 10L, Verdict(Parser, approved: false, "The parser drops the trailing field.")),
                (Cell("write-migration", ""), 11L, Verdict(Migration, approved: true, "Clean.")),
            },
            UnitLabels);

        fold.ShouldNotBeNull();
        fold!.Value.Approved.ShouldBe(false, "a run is verified only when EVERY reviewed unit approved — one flag is a flagged run");
        fold.Value.Reason.ShouldBe("The parser drops the trailing field.", "the FLAGGING reviewer's words are the ones that reach the card");
        fold.Value.Unit.ShouldBe("parser", "the fan-out NAMES the flagged branch — 'this result' is not something a reader of twelve branches can act on");
    }

    [Fact]
    public void A_SUPERVISOR_fan_outs_siblings_are_told_apart_even_though_they_SHARE_one_ledger_cell()
    {
        // A supervisor stamps its whole per-turn fan-out with ONE (NodeId, IterationKey) — `sup#turn1`, per TURN, not
        // per agent (RealSupervisorActionExecutor.Spawn). Keying the fold on the cell would therefore re-collapse K
        // sibling reviews into one and hand the verdict straight back to write order, on the very lane the fold exists
        // to fix. The beat names the agent run it is ABOUT, and that is the unit.
        var fold = RoomProjector.FoldReviewVerdicts(
            new[]
            {
                (Cell("sup", "sup#turn1"), 10L, Verdict(Parser, approved: false, "The parser drops the trailing field.")),
                (Cell("sup", "sup#turn1"), 11L, Verdict(Migration, approved: true, "Clean.")),
            },
            UnitLabels);

        fold!.Value.Approved.ShouldBe(false, "two agents, one turn cell — the later approval is a different unit's verdict, not this one's");
        fold.Value.Unit.ShouldBe("parser");
    }

    [Fact]
    public void A_beat_that_named_no_agent_run_still_folds_by_its_ledger_cell()
    {
        // The fallback: finer than the run, and on the map lane a cell already IS one branch.
        var fold = RoomProjector.FoldReviewVerdicts(
            new[]
            {
                (Cell("implement-parser", ""), 10L, Verdict(null, approved: false, "Off-spec.")),
                (Cell("write-migration", ""), 11L, Verdict(null, approved: true, "Clean.")),
            },
            UnitLabels);

        fold!.Value.Approved.ShouldBe(false);
        fold.Value.Unit.ShouldBe("parser", "the cell arm of the label map answers for a beat that named no agent run");
    }

    [Fact]
    public void The_flagged_branchs_name_reaches_the_chip_copy()
    {
        RoomProjector.Verification(graded: false, review: (false, "The parser drops the trailing field.", "parser")).Note
            .ShouldBe("Unverified — the output review flagged parser: The parser drops the trailing field.");
    }

    [Fact]
    public void EVERY_branch_approving_is_what_verifies_the_run()
    {
        var fold = RoomProjector.FoldReviewVerdicts(
            new[]
            {
                (Cell("implement-parser", ""), 10L, Verdict(Parser, approved: true, "Clean.")),
                (Cell("write-migration", ""), 11L, Verdict(Migration, approved: true, "Also clean.")),
            },
            UnitLabels);

        fold.ShouldBe((true, (string?)null, (string?)null));
    }

    [Fact]
    public void The_latest_beat_wins_PER_CELL_so_one_branchs_revise_round_cannot_settle_another()
    {
        // Improve mode, per branch: the parser was flagged then fixed (its own later beat approves), while the
        // migration was approved then flagged on re-review. Folding the run's latest beat alone would read whichever
        // of those four rows happened to be last; folding per unit reads each branch at ITS final word.
        var fold = RoomProjector.FoldReviewVerdicts(
            new[]
            {
                (Cell("implement-parser", ""), 10L, Verdict(Parser, approved: false, "Drops the trailing field.")),
                (Cell("write-migration", ""), 11L, Verdict(Migration, approved: true, "Clean.")),
                (Cell("implement-parser", ""), 12L, Verdict(Parser, approved: true, "The revision handles it.")),
                (Cell("write-migration", ""), 13L, Verdict(Migration, approved: false, "No backfill for the dropped column.")),
            },
            UnitLabels);

        fold!.Value.Approved.ShouldBe(false);
        fold.Value.Unit.ShouldBe("migrations", "the migration's LATEST word is the flag; the parser's latest word is its approval");
        fold.Value.Reason.ShouldBe("No backfill for the dropped column.");
    }

    [Fact]
    public void A_SINGLE_reviewed_unit_still_says_this_result()
    {
        // Byte-identity pin: with one reviewed unit there is nothing to disambiguate, so the copy is what it was.
        var fold = RoomProjector.FoldReviewVerdicts(new[] { (Cell("agent", ""), 7L, Verdict(Parser, approved: false, "Wrong answer.")) }, UnitLabels);

        fold!.Value.Unit.ShouldBeNull();
        RoomProjector.Verification(graded: false, fold).Note.ShouldBe("Unverified — the output review flagged this result: Wrong answer.");
    }

    [Fact]
    public void A_fanned_out_unit_no_phase_labelled_is_named_honestly_rather_than_by_its_raw_key()
    {
        var fold = RoomProjector.FoldReviewVerdicts(
            new[]
            {
                (Cell("gone-node", "map#4"), 10L, Verdict("00000000-0000-0000-0000-0000000000ff", approved: false, "Off-spec.")),
                (Cell("write-migration", ""), 11L, Verdict(Migration, approved: true, "Clean.")),
            },
            UnitLabels);

        fold!.Value.Unit.ShouldBe("an unnamed unit", "a raw agent-run id (or nodeId+iterationKey) is not a name a reader can use");
    }

    [Fact]
    public void No_beats_at_all_is_an_ABSENCE_not_a_verdict()
    {
        RoomProjector.FoldReviewVerdicts(Array.Empty<(string, long, string)>(), UnitLabels).ShouldBeNull();
    }

    // ── the per-UNIT fold: "did every graded unit PASS" ──

    [Fact]
    public void A_lane_whose_every_graded_unit_passed_folds_to_a_pass()
    {
        var fold = RoomProjector.UnitGrades(new[] { Unit(A, passed: true), Unit(B, passed: true) }, Labels);

        fold.Passed.ShouldBe(true);
        fold.Failed.ShouldBeEmpty();
    }

    [Fact]
    public void A_lane_with_ONE_rejected_unit_folds_to_a_failure_that_names_it()
    {
        // The defect: the fold asked whether any unit had a grade AT ALL (`is not null`), so a plan-map run under
        // `errorHandling: continue` reached Success with a REJECTED branch and painted the green, verified Result.
        var fold = RoomProjector.UnitGrades(new[] { Unit(A, passed: true), Unit(B, passed: false) }, Labels);

        fold.Passed.ShouldBe(false, "one rejected branch means the run's units did NOT all pass — presence of a grade is not a pass");
        fold.Failed.ShouldBe(new[] { "parser" });
    }

    [Fact]
    public void An_UNGRADED_lane_folds_to_null_because_absence_is_not_a_verdict()
    {
        RoomProjector.UnitGrades(new[] { Unit(A, passed: null), Unit(B, passed: null) }, Labels).Passed.ShouldBeNull();
        RoomProjector.UnitGrades(Array.Empty<SupervisorAgentResult>(), Labels).Passed.ShouldBeNull();
    }

    [Fact]
    public void A_VACUOUS_pass_is_not_a_graded_unit()
    {
        // "no changes were expected and none were produced" is the contract satisfied BY CONSTRUCTION — nothing ran,
        // so counting it launders "nothing to do" into "checked and correct".
        var fold = RoomProjector.UnitGrades(new[] { Unit(A, passed: true, detail: AgentAcceptanceContract.NotApplicableDetail) }, Labels);

        fold.Passed.ShouldBeNull("no check executed, so the card must fall through to its unverified copy");
    }

    [Fact]
    public void A_rejected_unit_no_phase_labelled_is_named_honestly_rather_than_by_raw_id()
    {
        var fold = RoomProjector.UnitGrades(new[] { Unit(Guid.NewGuid(), passed: false) }, Labels);

        fold.Failed.ShouldBe(new[] { "an unnamed unit" });
    }

    [Fact]
    public void A_WAIVED_unit_is_neither_a_rejection_nor_a_pass()
    {
        // A human authorized forgoing this unit's verification. Its executor-level grade can read FAILED — the waive is
        // precisely what let the work through anyway — so reading `AcceptancePassed is false` reported a rejection the
        // card had no business claiming. WAIVED ≠ PASSED either: the waived unit leaves the fold with NOTHING graded,
        // which is the honest answer, not a green one.
        var waived = Unit(B, passed: false) with { AcceptanceVerdict = VerificationDisposition.Waived };

        var alone = RoomProjector.UnitGrades(new[] { waived }, Labels);

        alone.Passed.ShouldBeNull("a waive verifies nothing — it must not fold to a pass and must not fold to a failure");
        alone.Failed.ShouldBeEmpty();

        var beside = RoomProjector.UnitGrades(new[] { Unit(A, passed: true), waived }, Labels);

        beside.Passed.ShouldBe(true, "the one unit that WAS graded passed; the waived one is simply not part of the fold");
        beside.Failed.ShouldBeEmpty("naming a waived unit as a failed check would report a rejection a human had already dispositioned");
    }

    [Theory]
    [InlineData(1, "Checks failed: u0")]
    [InlineData(3, "Checks failed: u0, u1, u2")]
    [InlineData(5, "Checks failed: u0, u1, u2 and 2 more")]
    public void The_reason_line_names_the_rejected_units_and_stays_one_line(int failedCount, string expected)
    {
        var units = Enumerable.Range(0, failedCount).Select(i => $"u{i}").ToArray();

        RoomProjector.ResultVerdict(acceptancePassed: false, Stop(SupervisorStopKind.Succeeded, summary: "All done."), units).Reason.ShouldBe(expected);
    }

    [Fact]
    public void A_long_unit_title_is_CLIPPED_so_the_one_line_stays_one_line()
    {
        // Bounding the COUNT at three bounded nothing on its own: these names are model-authored subtask titles, and a
        // single realistic one runs past a hundred characters into a small uppercase eyebrow built for a few words.
        var verdict = RoomProjector.ResultVerdict(
            acceptancePassed: false,
            Stop(SupervisorStopKind.Succeeded, summary: "All done."),
            new[] { "Implement the streaming parser and backfill every historical row that the old importer skipped" });

        verdict.Reason.ShouldBe("Checks failed: Implement the streaming parser and backf…", "clipped with an ellipsis, so the reader sees WHICH unit without the eyebrow wrapping");
        verdict.Reason!.Length.ShouldBeLessThan(60);
    }

    [Fact]
    public void Two_long_titles_that_differ_only_past_the_clip_stay_two_names()
    {
        // Clipping BEFORE the de-dup is what keeps "…and 2 more" honest: two titles sharing a long prefix must not
        // collapse into one name, and must not print the same clipped string twice either.
        var verdict = RoomProjector.ResultVerdict(
            acceptancePassed: false,
            Stop(SupervisorStopKind.Succeeded, summary: "All done."),
            new[] { new string('x', 60) + " alpha", new string('x', 60) + " beta" });

        verdict.Reason.ShouldBe($"Checks failed: {new string('x', 40)}…", "identical after the clip ⇒ ONE name, not the same string printed twice");
    }

    [Fact]
    public void The_SUPERVISOR_lanes_reason_line_is_byte_identical()
    {
        // The pin: a supervisor run passes no unit names (its stop grade is the head's own verdict), so its card reads
        // exactly as it did before the per-unit fold existed.
        RoomProjector.ResultVerdict(acceptancePassed: false, Stop(SupervisorStopKind.Succeeded, summary: "Shipped.")).Reason.ShouldBe("Checks failed");
        RoomProjector.ResultVerdict(acceptancePassed: false, Stop(SupervisorStopKind.Succeeded, summary: "Shipped."), Array.Empty<string>()).Reason.ShouldBe("Checks failed");
    }

    [Fact]
    public void A_supervisor_run_whose_units_ALL_passed_renders_byte_identically()
    {
        // Byte-identity pin for the untouched lane: an all-passed supervisor run is verified with no note, exactly as
        // before — the fold changes what a REJECTED unit means, never what a clean one does.
        var fold = RoomProjector.UnitGrades(new[] { Unit(A, passed: true), Unit(B, passed: true) }, Labels);

        RoomProjector.ResultVerdict(acceptancePassed: true, Stop(SupervisorStopKind.Succeeded, summary: "Shipped.")).ShouldBe((false, (string?)null));
        RoomProjector.Verification(graded: fold.Passed is true, review: null).ShouldBe(((bool?)true, (string?)null));
    }

    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, string> Labels = new Dictionary<Guid, string> { [A] = "migrations", [B] = "parser" };

    private static SupervisorAgentResult Unit(Guid id, bool? passed, string? detail = null) =>
        new() { AgentRunId = id, Status = "Succeeded", AcceptancePassed = passed, AcceptanceDetail = detail };

    /// <summary>The PRODUCTION cell key, called directly — a mirror of it here would pass while the fold and the projector keyed different cells.</summary>
    private static string Cell(string nodeId, string iterationKey) => RoomProjector.CellKey(nodeId, iterationKey);

    private const string Migration = "1f0a0000-0000-0000-0000-000000000001";
    private const string Parser = "1f0a0000-0000-0000-0000-000000000002";

    /// <summary>The reviewed units' display names, keyed BOTH ways the projector keys them — by agent-run id (what the beat names) and by ledger cell (the fallback).</summary>
    private static readonly IReadOnlyDictionary<string, string> UnitLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Migration] = "migrations",
        [Parser] = "parser",
        [Cell("write-migration", "")] = "migrations",
        [Cell("implement-parser", "")] = "parser",
    };

    /// <summary>One <c>review.completed</c> payload, in the shape <c>AgentRunExecutor</c> writes it.</summary>
    private static string Verdict(string? agentRunId, bool approved, string reason) =>
        JsonSerializer.Serialize(new { kind = "critic.output", agentRunId, approved, reason });

    /// <summary>One <c>review.skipped</c> payload, in the shape <c>LlmStructuredCritic.RecordSkippedAsync</c> writes it — no <c>agentRunId</c> key at all, so the fold always falls back to the ledger cell.</summary>
    private static string SkippedVerdict(string reason) =>
        JsonSerializer.Serialize(new { kind = "critic.skipped", mode = "Gate", artifact_kind = "agent change", reason });
}
