using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: the F0 completion reducer's conformance matrix (v4.1-F) — every semantic decision in
/// <see cref="CompletionReducer"/> is pinned HERE, because the reducer is THE single verdict every consumer
/// converges onto: a silent change to any arm silently rewrites the north-star, the gates, and every renderer at
/// once. Covers: execution classification precedence, the verification severity order (incl. WAIVED ≠ PASSED),
/// required-vs-optional receipt handling, the ModelProposal kernel exclusion, the outcome mapping (incl. the
/// status-fallback parity arm the 2b consumer switch relies on), artifact/delivery folds, the LegacyUnknown
/// projection, the cutover pin, and the IsTerminalizable precondition.
/// </summary>
[Trait("Category", "Unit")]
public class CompletionReducerTests
{
    // ── Execution classification ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_cancelled_run_is_Cancelled_even_when_forced_and_disorderly()
    {
        var a = CompletionReducer.Reduce(None, NoReceipts, Facts(WorkflowRunStatus.Cancelled, forcedStopReason: "cost cap reached", orderly: false));

        a.Execution.ShouldBe(ExecutionDisposition.Cancelled);
        a.ForcedStopReason.ShouldBeNull("the reason is only surfaced on a ForcedStop execution");
    }

    [Fact]
    public void A_forced_stop_reason_classifies_ForcedStop_and_surfaces_the_reason()
    {
        var a = CompletionReducer.Reduce(None, NoReceipts, Facts(WorkflowRunStatus.Success, forcedStopReason: "no forward progress"));

        a.Execution.ShouldBe(ExecutionDisposition.ForcedStop);
        a.ForcedStopReason.ShouldBe("no forward progress");
    }

    [Fact]
    public void A_recorded_forced_stop_outranks_a_disorderly_tape()
    {
        // A forced-stop reason only exists because a stop decision was durably recorded — that recorded intent
        // outranks a missing orderly-terminal signal (contradictory composer facts resolve toward the tape).
        CompletionReducer.Reduce(None, NoReceipts, Facts(WorkflowRunStatus.Failure, forcedStopReason: "cost cap reached", orderly: false)).Execution.ShouldBe(ExecutionDisposition.ForcedStop);
    }

    [Fact]
    public void A_missing_orderly_terminal_is_an_engine_death()
    {
        CompletionReducer.Reduce(None, NoReceipts, Facts(WorkflowRunStatus.Failure, orderly: false)).Execution.ShouldBe(ExecutionDisposition.Crashed);
    }

    [Fact]
    public void A_failure_that_ran_its_course_is_still_Completed()
    {
        // Execution says HOW it ended, never whether it succeeded — the failing is Outcome/Verification business.
        CompletionReducer.Reduce(None, NoReceipts, Facts(WorkflowRunStatus.Failure)).Execution.ShouldBe(ExecutionDisposition.Completed);
    }

    // ── Verification aggregation ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Zero_acceptance_requirements_read_NotApplicable_even_beside_other_kinds()
    {
        var requirements = new[] { Requirement("d", ContractKinds.Delivery), Requirement("o", ContractKinds.Output) };

        CompletionReducer.Reduce(requirements, NoReceipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(VerificationDisposition.NotApplicable);
    }

    [Theory]
    [InlineData(VerificationDisposition.Failed, VerificationDisposition.Passed, VerificationDisposition.Failed)]
    [InlineData(VerificationDisposition.Failed, VerificationDisposition.InfraUnknown, VerificationDisposition.Failed)]
    [InlineData(VerificationDisposition.InfraUnknown, VerificationDisposition.Passed, VerificationDisposition.InfraUnknown)]
    [InlineData(VerificationDisposition.HumanReviewRequired, VerificationDisposition.Passed, VerificationDisposition.HumanReviewRequired)]
    [InlineData(VerificationDisposition.InfraUnknown, VerificationDisposition.HumanReviewRequired, VerificationDisposition.InfraUnknown)]
    [InlineData(VerificationDisposition.Waived, VerificationDisposition.Passed, VerificationDisposition.Waived)]
    [InlineData(VerificationDisposition.HumanReviewRequired, VerificationDisposition.Waived, VerificationDisposition.HumanReviewRequired)]
    [InlineData(VerificationDisposition.Failed, VerificationDisposition.Unknown, VerificationDisposition.Failed)]
    [InlineData(VerificationDisposition.Unknown, VerificationDisposition.InfraUnknown, VerificationDisposition.Unknown)]
    public void The_severity_order_folds_worst_first(VerificationDisposition first, VerificationDisposition second, VerificationDisposition expected)
    {
        var requirements = new[] { Requirement("a1"), Requirement("a2") };
        var receipts = new[] { Receipt("a1", first), Receipt("a2", second) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(expected);
    }

    [Fact]
    public void A_waiver_beside_passes_never_launders_into_Passed()
    {
        // The amend-acceptance FATAL-1 invariant, at the aggregate grain: one waived requirement among passing
        // ones must surface Waived — an objective-truth reader may NEVER see a waived contract as fully verified.
        var requirements = new[] { Requirement("a1"), Requirement("a2") };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Passed), Receipt("a2", VerificationDisposition.Waived) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(VerificationDisposition.Waived);
    }

    [Fact]
    public void A_decided_failure_beside_an_unanswered_requirement_stays_Failed_and_terminalizes()
    {
        // Failed outranks Unknown in the fold — a decided failure must terminalize as a Failure, never park
        // forever behind a sibling requirement's missing receipt.
        var requirements = new[] { Requirement("failed"), Requirement("unanswered") };
        var receipts = new[] { Receipt("failed", VerificationDisposition.Failed) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Failure));

        a.Verification.ShouldBe(VerificationDisposition.Failed);
        a.Outcome.ShouldBe(OutcomeDisposition.Unsolved);
        CompletionReducer.IsTerminalizable(a).ShouldBeTrue();
    }

    [Fact]
    public void A_required_requirement_with_no_receipt_reads_Unknown_and_outranks_InfraUnknown()
    {
        var requirements = new[] { Requirement("answered"), Requirement("unanswered") };
        var receipts = new[] { Receipt("answered", VerificationDisposition.InfraUnknown) };

        // A required check with NO story at all is a deeper truth hole than one whose machinery broke mid-run.
        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(VerificationDisposition.Unknown);
    }

    [Fact]
    public void A_missing_optional_receipt_is_ignored_but_a_present_optional_failure_lowers_the_dim()
    {
        var requirements = new[] { Requirement("req"), Requirement("opt", requiredness: Requiredness.Optional) };

        var withoutOptional = CompletionReducer.Reduce(requirements, new[] { Receipt("req", VerificationDisposition.Passed) }, Facts(WorkflowRunStatus.Success));
        withoutOptional.Verification.ShouldBe(VerificationDisposition.Passed, "an unanswered OPTIONAL requirement never blocks");

        var withOptionalFailure = CompletionReducer.Reduce(requirements, new[] { Receipt("req", VerificationDisposition.Passed), Receipt("opt", VerificationDisposition.Failed) }, Facts(WorkflowRunStatus.Success));
        withOptionalFailure.Verification.ShouldBe(VerificationDisposition.Failed, "a PRESENT optional receipt participates in the fold");
    }

    [Fact]
    public void A_waiver_on_an_optional_requirement_still_demotes_the_run_to_Abstained()
    {
        // Deliberate: a PRESENT optional receipt participates in the fold, and Waived outranks Passed — a human
        // waiver anywhere in the contract removes the run's objective claim (it can never hide a failure:
        // Failed outranks Waived).
        var requirements = new[] { Requirement("req"), Requirement("opt", requiredness: Requiredness.Optional) };
        var receipts = new[] { Receipt("req", VerificationDisposition.Passed), Receipt("opt", VerificationDisposition.Waived) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Outcome.ShouldBe(OutcomeDisposition.Abstained);
    }

    [Fact]
    public void Only_optional_requirements_all_unanswered_read_NotApplicable()
    {
        // Deliberate consequence, pinned: Optional-never-blocks means the run falls back to its honest end —
        // a Success run with a declared-but-unanswered OPTIONAL oracle reads Solved via the fallback arm.
        var requirements = new[] { Requirement("opt", requiredness: Requiredness.Optional) };

        var a = CompletionReducer.Reduce(requirements, NoReceipts, Facts(WorkflowRunStatus.Success));

        a.Verification.ShouldBe(VerificationDisposition.NotApplicable);
        a.Outcome.ShouldBe(OutcomeDisposition.Solved);
    }

    [Fact]
    public void Multiple_receipts_for_one_requirement_fold_worst_first()
    {
        // ExpectedCardinality > 1 (a multi-repo acceptance): a single failing target fails the requirement.
        var requirements = new[] { Requirement("multi") };
        var receipts = new[] { Receipt("multi", VerificationDisposition.Passed), Receipt("multi", VerificationDisposition.Failed) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(VerificationDisposition.Failed);
    }

    [Theory]
    [InlineData(VerificationDisposition.Passed, VerificationDisposition.Unknown)]   // 1-of-3 verified is NOT verified
    [InlineData(VerificationDisposition.Failed, VerificationDisposition.Failed)]    // a definite failure still outranks the shortfall
    public void An_under_delivered_cardinality_is_a_truth_hole_not_a_pass(VerificationDisposition received, VerificationDisposition expected)
    {
        var requirements = new[] { Requirement("multi") with { ExpectedCardinality = 3 } };
        var receipts = new[] { Receipt("multi", received) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(expected);
    }

    [Fact]
    public void A_met_cardinality_folds_its_receipts_normally()
    {
        var requirements = new[] { Requirement("multi") with { ExpectedCardinality = 2 } };
        var receipts = new[] { Receipt("multi", VerificationDisposition.Passed), Receipt("multi", VerificationDisposition.Passed) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(VerificationDisposition.Passed);
    }

    [Fact]
    public void Over_delivery_is_not_a_truth_hole()
    {
        // A retry double-write (two receipts against cardinality 1) must not inject Unknown and park the run —
        // only UNDER-delivery is a hole.
        var requirements = new[] { Requirement("single") };
        var receipts = new[] { Receipt("single", VerificationDisposition.Passed), Receipt("single", VerificationDisposition.Passed) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(VerificationDisposition.Passed);
    }

    [Fact]
    public void A_corrupt_declared_cardinality_clamps_to_one()
    {
        // The kernel cannot reconstruct an authored cardinality from a corrupt (sub-1) value — it clamps to 1;
        // authorship integrity is mint-time validation's job.
        var requirements = new[] { Requirement("c") with { ExpectedCardinality = 0 } };
        var receipts = new[] { Receipt("c", VerificationDisposition.Passed) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(VerificationDisposition.Passed);
    }

    [Fact]
    public void Delivery_and_output_receipts_never_move_the_verification_dim()
    {
        var requirements = new[] { Requirement("a1"), Requirement("d", ContractKinds.Delivery), Requirement("o", ContractKinds.Output) };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Passed), Receipt("d", VerificationDisposition.Failed, ContractKinds.Delivery), Receipt("o", VerificationDisposition.Failed, ContractKinds.Output) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(VerificationDisposition.Passed);
    }

    [Fact]
    public void An_inapplicability_claim_on_an_acceptance_requirement_cannot_reach_the_status_fallback()
    {
        // The vacuous-pass reclassification is RESERVED for its own test-pinned PR (B0): until then an
        // acceptance receipt claiming NotApplicable reads Unknown — it can neither launder a Success status
        // into Solved via the no-oracle fallback arm nor punish the run as Unsolved.
        var requirements = new[] { Requirement("a1") };
        var receipts = new[] { Receipt("a1", VerificationDisposition.NotApplicable) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Verification.ShouldBe(VerificationDisposition.Unknown);
        a.Outcome.ShouldBe(OutcomeDisposition.Unknown);
    }

    [Fact]
    public void A_corrupt_disposition_reads_Unknown_instead_of_throwing()
    {
        var requirements = new[] { Requirement("a1") };
        var receipts = new[] { Receipt("a1", (VerificationDisposition)99) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification
            .ShouldBe(VerificationDisposition.Unknown, "the reducer degrades to unstatable truth on a corrupt tape value — it never throws");
    }

    [Fact]
    public void A_corrupt_disposition_beside_a_pass_is_never_masked()
    {
        // Sanitization is PER ELEMENT: a corrupt value (a receipt written under a newer enum version) reads
        // Unknown before ranking, so a sibling Passed cannot outrank and silently swallow it.
        var requirements = new[] { Requirement("multi") with { ExpectedCardinality = 2 } };
        var receipts = new[] { Receipt("multi", VerificationDisposition.Passed), Receipt("multi", (VerificationDisposition)99) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification.ShouldBe(VerificationDisposition.Unknown);
    }

    [Fact]
    public void A_receipt_of_one_kind_never_answers_a_same_ref_requirement_of_another()
    {
        // Refs are unique only WITHIN a kind (run-level delivery/output use fixed keys) — kind filtering in the
        // receipt lookup is load-bearing.
        var requirements = new[] { Requirement("shared"), Requirement("shared", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("shared", VerificationDisposition.Passed) };   // acceptance kind only

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Verification.ShouldBe(VerificationDisposition.Passed);
        a.Delivery.ShouldBe(DeliveryDisposition.Unknown, "the acceptance receipt must not satisfy the delivery obligation");
    }

    [Fact]
    public void Ref_matching_is_case_sensitive()
    {
        var requirements = new[] { Requirement("A1") };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Passed) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Verification
            .ShouldBe(VerificationDisposition.Unknown, "a case-differing ref is a different ref — mismatch degrades conservatively");
    }

    [Fact]
    public void A_required_requirement_of_an_unrouted_kind_degrades_the_outcome_and_parks()
    {
        // The fixed five-dimension projection fails LOUD at its boundary: an obligation the kernel cannot route
        // (a fourth kind) must never be silently invisible — the run reads Unknown and parks instead of
        // terminalizing as decided.
        var requirements = new[] { Requirement("a1"), Requirement("h", kind: "review.human") };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Passed) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Verification.ShouldBe(VerificationDisposition.Passed, "the routed dimensions still project honestly");
        a.Outcome.ShouldBe(OutcomeDisposition.Unknown);
        CompletionReducer.IsTerminalizable(a).ShouldBeFalse();
    }

    [Fact]
    public void An_optional_unrouted_kind_never_blocks()
    {
        var requirements = new[] { Requirement("a1"), Requirement("h", kind: "review.human", requiredness: Requiredness.Optional) };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Passed) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Outcome.ShouldBe(OutcomeDisposition.Solved);
    }

    [Fact]
    public void The_routed_kind_set_is_pinned()
    {
        // The guard's boundary IS this list — widening it without a reducer dimension route would resurrect the
        // silent-drop hole.
        ContractKinds.Routed.ShouldBe(new[] { ContractKinds.Acceptance, ContractKinds.Delivery, ContractKinds.Output });
    }

    [Fact]
    public void A_run_owing_a_required_delivery_with_no_receipt_cannot_clean_terminal_as_Solved()
    {
        // The C1 hole, closed: no acceptance requirement + Success would take the status-fallback arm to Solved
        // while a required delivery (and output) sits unanswered — the run must park, not terminalize.
        var requirements = new[] { Requirement("d", ContractKinds.Delivery), Requirement("o", ContractKinds.Output) };

        var a = CompletionReducer.Reduce(requirements, NoReceipts, Facts(WorkflowRunStatus.Success));

        a.Delivery.ShouldBe(DeliveryDisposition.Unknown);
        a.Artifact.ShouldBe(ArtifactDisposition.Unknown);
        a.Outcome.ShouldBe(OutcomeDisposition.Unknown);
        CompletionReducer.IsTerminalizable(a).ShouldBeFalse();
    }

    [Fact]
    public void A_settled_required_delivery_restores_the_fallback_solve()
    {
        var requirements = new[] { Requirement("d", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("d", VerificationDisposition.Passed, ContractKinds.Delivery) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Delivery.ShouldBe(DeliveryDisposition.Delivered);
        a.Outcome.ShouldBe(OutcomeDisposition.Solved);
        CompletionReducer.IsTerminalizable(a).ShouldBeTrue();
    }

    [Fact]
    public void A_policy_blocked_delivery_is_settled_and_does_not_block_the_outcome()
    {
        // PolicyBlocked is a DECIDED state — the delivery dimension carries its story; the outcome is not held
        // hostage by an obligation policy already adjudicated.
        var requirements = new[] { Requirement("d", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("d", VerificationDisposition.Failed, ContractKinds.Delivery) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Delivery.ShouldBe(DeliveryDisposition.PolicyBlocked);
        a.Outcome.ShouldBe(OutcomeDisposition.Solved);
    }

    [Fact]
    public void A_waiver_cannot_discharge_an_unsettled_required_delivery()
    {
        // Waiving the ORACLE does not waive the delivery obligation — an Abstained outcome with a required
        // delivery unanswered degrades to Unknown and parks.
        var requirements = new[] { Requirement("a1"), Requirement("d", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Waived) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Outcome.ShouldBe(OutcomeDisposition.Unknown);
        CompletionReducer.IsTerminalizable(a).ShouldBeFalse();
    }

    [Fact]
    public void A_decided_failure_survives_an_unsettled_obligation()
    {
        var requirements = new[] { Requirement("a1"), Requirement("d", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Failed) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Failure));

        a.Outcome.ShouldBe(OutcomeDisposition.Unsolved, "a decided failure is stronger evidence than a truth hole");
        CompletionReducer.IsTerminalizable(a).ShouldBeTrue();
    }

    [Fact]
    public void An_optional_delivery_never_holds_the_outcome_hostage()
    {
        var requirements = new[] { Requirement("d", ContractKinds.Delivery, Requiredness.Optional) };

        CompletionReducer.Reduce(requirements, NoReceipts, Facts(WorkflowRunStatus.Success)).Outcome.ShouldBe(OutcomeDisposition.Solved);
    }

    [Fact]
    public void A_corrupt_authority_receipt_fails_closed_like_a_self_report()
    {
        // The authority gate is an ALLOWLIST: a corrupt enum value can never mint success — the requirement reads
        // Unknown exactly as unanswered.
        var requirements = new[] { Requirement("a1") };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Passed, authority: (ContractAuthority)99) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Verification.ShouldBe(VerificationDisposition.Unknown);
        a.Outcome.ShouldBe(OutcomeDisposition.Unknown);
    }

    // ── The ModelProposal kernel exclusion ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_self_reported_pass_never_satisfies_a_required_requirement()
    {
        // "A model may propose, never authorize" — a ModelProposal receipt is invisible to every objective
        // dimension: a required requirement answered ONLY by a self-report reads Unknown, exactly as unanswered.
        var requirements = new[] { Requirement("a1") };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Passed, authority: ContractAuthority.ModelProposal) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Verification.ShouldBe(VerificationDisposition.Unknown);
        a.Outcome.ShouldBe(OutcomeDisposition.Unknown);
    }

    [Fact]
    public void A_self_report_cannot_mint_an_output_exemption_or_a_delivery()
    {
        var requirements = new[] { Requirement("o", ContractKinds.Output), Requirement("d", ContractKinds.Delivery) };
        var receipts = new[]
        {
            Receipt("o", VerificationDisposition.NotApplicable, ContractKinds.Output, ContractAuthority.ModelProposal),
            Receipt("d", VerificationDisposition.Passed, ContractKinds.Delivery, ContractAuthority.ModelProposal),
        };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Artifact.ShouldBe(ArtifactDisposition.Unknown, "a model cannot authorize NothingExpected");
        a.Delivery.ShouldBe(DeliveryDisposition.Unknown, "a model cannot self-certify a delivery");
    }

    // ── Outcome mapping ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(VerificationDisposition.Passed, OutcomeDisposition.Solved)]
    [InlineData(VerificationDisposition.Failed, OutcomeDisposition.Unsolved)]
    [InlineData(VerificationDisposition.Waived, OutcomeDisposition.Abstained)]
    [InlineData(VerificationDisposition.InfraUnknown, OutcomeDisposition.Unknown)]
    [InlineData(VerificationDisposition.HumanReviewRequired, OutcomeDisposition.Unknown)]
    public void A_verification_verdict_is_authoritative_for_the_outcome(VerificationDisposition verification, OutcomeDisposition expected)
    {
        var requirements = new[] { Requirement("a1") };
        var receipts = new[] { Receipt("a1", verification) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Outcome.ShouldBe(expected);
    }

    [Fact]
    public void A_waived_contract_is_Abstained_never_Solved_and_never_Unsolved()
    {
        // WAIVED ≠ PASSED at the outcome grain: a human authorized forgoing verification — the run makes no
        // objective claim in either direction, so a waiver can neither move a solve-rate nor punish it.
        var requirements = new[] { Requirement("a1") };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Waived) };

        var a = CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success));

        a.Outcome.ShouldBe(OutcomeDisposition.Abstained);
        CompletionReducer.IsTerminalizable(a).ShouldBeTrue("a human-abstained outcome always terminalizes");
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Success, true, null, OutcomeDisposition.Solved)]     // no contract + honest Success → the scorecard parity arm
    [InlineData(WorkflowRunStatus.Failure, true, null, OutcomeDisposition.Unsolved)]
    [InlineData(WorkflowRunStatus.Cancelled, true, null, OutcomeDisposition.Unsolved)]
    [InlineData(WorkflowRunStatus.Failure, false, null, OutcomeDisposition.Unsolved)]  // crashed
    [InlineData(WorkflowRunStatus.Success, true, "no forward progress", OutcomeDisposition.Unsolved)]  // forced stop is never a fallback solve, even at engine Success
    public void With_no_contract_the_outcome_falls_back_to_the_honest_end(WorkflowRunStatus status, bool orderly, string? forced, OutcomeDisposition expected)
    {
        CompletionReducer.Reduce(None, NoReceipts, Facts(status, forced, orderly)).Outcome.ShouldBe(expected);
    }

    // ── Artifact fold ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_output_requirement_reads_Unknown_never_NothingExpected()
    {
        CompletionReducer.Reduce(None, NoReceipts, Facts(WorkflowRunStatus.Success)).Artifact.ShouldBe(ArtifactDisposition.Unknown);
    }

    [Theory]
    [InlineData(VerificationDisposition.Passed, ArtifactDisposition.Captured)]
    [InlineData(VerificationDisposition.Failed, ArtifactDisposition.CaptureFailed)]
    [InlineData(VerificationDisposition.NotApplicable, ArtifactDisposition.NothingExpected)]   // the AUTHORIZED exemption encoding
    [InlineData(VerificationDisposition.InfraUnknown, ArtifactDisposition.Unknown)]
    public void An_output_receipt_maps_onto_the_artifact_dim(VerificationDisposition disposition, ArtifactDisposition expected)
    {
        var requirements = new[] { Requirement("o", ContractKinds.Output) };
        var receipts = new[] { Receipt("o", disposition, ContractKinds.Output) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Artifact.ShouldBe(expected);
    }

    [Fact]
    public void Content_hashes_prove_capture_and_a_definite_failure_outranks_them()
    {
        var requirements = new[] { Requirement("o", ContractKinds.Output) };

        var hashed = new[] { Receipt("o", VerificationDisposition.Unknown, ContractKinds.Output) with { ContentHashes = new[] { "abc123" } } };
        CompletionReducer.Reduce(requirements, hashed, Facts(WorkflowRunStatus.Success)).Artifact.ShouldBe(ArtifactDisposition.Captured);

        var mixed = new[] { hashed[0], Receipt("o", VerificationDisposition.Failed, ContractKinds.Output) };
        CompletionReducer.Reduce(requirements, mixed, Facts(WorkflowRunStatus.Success)).Artifact.ShouldBe(ArtifactDisposition.CaptureFailed);
    }

    [Theory]
    [InlineData(VerificationDisposition.Failed, ArtifactDisposition.CaptureFailed)]        // a failed capture is never laundered by partial hashes
    [InlineData(VerificationDisposition.Waived, ArtifactDisposition.Unknown)]              // Waived is NEVER rewritten toward Passed inside the kernel (FATAL-1)
    [InlineData(VerificationDisposition.NotApplicable, ArtifactDisposition.NothingExpected)]   // an authorized exemption beats an incidental hash
    public void The_hash_upgrade_lifts_only_a_verdict_less_receipt(VerificationDisposition disposition, ArtifactDisposition expected)
    {
        var requirements = new[] { Requirement("o", ContractKinds.Output) };
        var receipts = new[] { Receipt("o", disposition, ContractKinds.Output) with { ContentHashes = new[] { "abc123" } } };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Artifact.ShouldBe(expected);
    }

    [Fact]
    public void A_corrupt_output_disposition_is_never_lifted_by_hashes()
    {
        var requirements = new[] { Requirement("o", ContractKinds.Output) };
        var receipts = new[] { Receipt("o", (VerificationDisposition)99, ContractKinds.Output) with { ContentHashes = new[] { "abc123" } } };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Artifact.ShouldBe(ArtifactDisposition.Unknown);
    }

    [Fact]
    public void One_captured_output_cannot_answer_a_siblings_unanswered_requirement()
    {
        // The hash upgrade lifts only the receipt it evidences — a hole-detection Unknown from an unanswered
        // sibling requirement survives the fold.
        var requirements = new[] { Requirement("o1", ContractKinds.Output), Requirement("o2", ContractKinds.Output) };
        var receipts = new[] { Receipt("o1", VerificationDisposition.Unknown, ContractKinds.Output) with { ContentHashes = new[] { "abc123" } } };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Artifact.ShouldBe(ArtifactDisposition.Unknown);
    }

    // ── Delivery fold ───────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_delivery_requirement_reads_NotRequired()
    {
        CompletionReducer.Reduce(None, NoReceipts, Facts(WorkflowRunStatus.Success)).Delivery.ShouldBe(DeliveryDisposition.NotRequired);
    }

    [Theory]
    [InlineData(VerificationDisposition.Passed, DeliveryDisposition.Delivered)]
    [InlineData(VerificationDisposition.Waived, DeliveryDisposition.WaivedByPolicy)]   // a waiver is never a delivery
    [InlineData(VerificationDisposition.Failed, DeliveryDisposition.PolicyBlocked)]
    [InlineData(VerificationDisposition.InfraUnknown, DeliveryDisposition.Unknown)]
    public void A_delivery_receipt_maps_onto_the_delivery_dim(VerificationDisposition disposition, DeliveryDisposition expected)
    {
        var requirements = new[] { Requirement("d", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("d", disposition, ContractKinds.Delivery) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Delivery.ShouldBe(expected);
    }

    [Fact]
    public void An_unanswered_required_delivery_beside_a_delivered_one_reads_Unknown()
    {
        // Per-requirement matching (never kind-level pooling): one delivered repo cannot answer for a second
        // repo's unanswered delivery obligation.
        var requirements = new[] { Requirement("d1", ContractKinds.Delivery), Requirement("d2", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("d1", VerificationDisposition.Passed, ContractKinds.Delivery) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Delivery.ShouldBe(DeliveryDisposition.Unknown);
    }

    [Fact]
    public void An_orphan_receipt_matching_no_requirement_mints_nothing()
    {
        // A receipt cannot mint an obligation, let alone satisfy one — an orphan delivery receipt leaves the
        // dim NotRequired, and an orphan acceptance receipt leaves it NotApplicable.
        var receipts = new[] { Receipt("ghost", VerificationDisposition.Passed, ContractKinds.Delivery), Receipt("ghost2", VerificationDisposition.Passed) };

        var a = CompletionReducer.Reduce(None, receipts, Facts(WorkflowRunStatus.Success));

        a.Delivery.ShouldBe(DeliveryDisposition.NotRequired);
        a.Verification.ShouldBe(VerificationDisposition.NotApplicable);
    }

    [Fact]
    public void A_delivered_repo_beside_an_authorized_exemption_reads_Delivered()
    {
        // Pins the severity-order TAIL (Passed outranks NotApplicable): one delivered obligation beside one
        // authorized "no delivery owed" reads Delivered, never NotRequired.
        var requirements = new[] { Requirement("d1", ContractKinds.Delivery), Requirement("d2", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("d1", VerificationDisposition.Passed, ContractKinds.Delivery), Receipt("d2", VerificationDisposition.NotApplicable, ContractKinds.Delivery) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Delivery.ShouldBe(DeliveryDisposition.Delivered);
    }

    [Fact]
    public void An_authorized_not_applicable_delivery_reads_NotRequired()
    {
        var requirements = new[] { Requirement("d", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("d", VerificationDisposition.NotApplicable, ContractKinds.Delivery) };

        CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success)).Delivery.ShouldBe(DeliveryDisposition.NotRequired);
    }

    [Fact]
    public void A_required_delivery_with_no_receipt_reads_Unknown()
    {
        var requirements = new[] { Requirement("d", ContractKinds.Delivery) };

        CompletionReducer.Reduce(requirements, NoReceipts, Facts(WorkflowRunStatus.Success)).Delivery.ShouldBe(DeliveryDisposition.Unknown);
    }

    // ── LegacyUnknown projection + cutover pin ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_legacy_projection_derives_only_execution()
    {
        var a = CompletionReducer.ReduceLegacy(Facts(WorkflowRunStatus.Success, forcedStopReason: "cost cap reached"));

        a.Basis.ShouldBe(CompletionBasis.LegacyUnknown);
        a.Execution.ShouldBe(ExecutionDisposition.ForcedStop);
        a.ForcedStopReason.ShouldBe("cost cap reached");
        a.Outcome.ShouldBe(OutcomeDisposition.Unknown);
        a.Verification.ShouldBe(VerificationDisposition.Unknown);
        a.Artifact.ShouldBe(ArtifactDisposition.Unknown);
        a.Delivery.ShouldBe(DeliveryDisposition.Unknown);
    }

    [Fact]
    public void The_cutover_value_is_pinned()
    {
        // Moving the cutover silently REWRITES which runs have contract truth — the value change must be an
        // explicit, reviewed decision, never a refactor side-effect.
        CompletionCutover.Value.ShouldBe(new DateTimeOffset(2026, 7, 13, 0, 0, 0, TimeSpan.Zero));

        CompletionCutover.IsContractEra(CompletionCutover.Value).ShouldBeTrue("the boundary instant itself is contract-era");
        CompletionCutover.IsContractEra(CompletionCutover.Value.AddTicks(-1)).ShouldBeFalse();
    }

    [Fact]
    public void A_contract_era_reduction_is_marked_ContractDerived()
    {
        CompletionReducer.Reduce(None, NoReceipts, Facts(WorkflowRunStatus.Success)).Basis.ShouldBe(CompletionBasis.ContractDerived);
    }

    // ── IsTerminalizable ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_orderly_completion_with_unknown_truth_must_not_terminalize()
    {
        var requirements = new[] { Requirement("a1") };   // required, never answered → Verification Unknown → Outcome Unknown

        var a = CompletionReducer.Reduce(requirements, NoReceipts, Facts(WorkflowRunStatus.Success));

        a.Outcome.ShouldBe(OutcomeDisposition.Unknown);
        CompletionReducer.IsTerminalizable(a).ShouldBeFalse("a run claiming an orderly finish with unstatable truth parks for adjudication");
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Cancelled, null, true)]
    [InlineData(WorkflowRunStatus.Failure, null, false)]                       // crashed
    [InlineData(WorkflowRunStatus.Success, "no forward progress", true)]       // forced stop
    public void Every_honest_end_terminalizes_even_with_unknown_truth(WorkflowRunStatus status, string? forced, bool orderly)
    {
        var requirements = new[] { Requirement("a1") };

        var a = CompletionReducer.Reduce(requirements, NoReceipts, Facts(status, forced, orderly));

        a.Outcome.ShouldBe(OutcomeDisposition.Unknown);
        CompletionReducer.IsTerminalizable(a).ShouldBeTrue("park-don't-die never blocks recording a death");
    }

    [Fact]
    public void A_decided_completion_terminalizes()
    {
        var requirements = new[] { Requirement("a1") };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Passed) };

        CompletionReducer.IsTerminalizable(CompletionReducer.Reduce(requirements, receipts, Facts(WorkflowRunStatus.Success))).ShouldBeTrue();
    }

    // ── Determinism (the v4.1-F exit assertion) ─────────────────────────────────────────────────────────────

    [Fact]
    public void Same_contract_and_same_facts_produce_the_same_assessment()
    {
        var requirements = new[] { Requirement("a1"), Requirement("d", ContractKinds.Delivery) };
        var receipts = new[] { Receipt("a1", VerificationDisposition.Passed), Receipt("d", VerificationDisposition.Passed, ContractKinds.Delivery) };
        var facts = Facts(WorkflowRunStatus.Success);

        CompletionReducer.Reduce(requirements, receipts, facts).ShouldBe(CompletionReducer.Reduce(requirements, receipts, facts));
    }

    // ── Builders ────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly IReadOnlyList<RequirementEnvelope> None = Array.Empty<RequirementEnvelope>();
    private static readonly IReadOnlyList<ReceiptEnvelope> NoReceipts = Array.Empty<ReceiptEnvelope>();

    private static CompletionRunFacts Facts(WorkflowRunStatus status, string? forcedStopReason = null, bool orderly = true) => new()
    {
        TerminalStatus = status,
        ForcedStopReason = forcedStopReason,
        HadOrderlyTerminal = orderly,
    };

    private static RequirementEnvelope Requirement(string requirementRef, string kind = ContractKinds.Acceptance, Requiredness requiredness = Requiredness.Required) => new()
    {
        RequirementRef = requirementRef,
        Kind = kind,
        Requiredness = requiredness,
        Authority = ContractAuthority.Operator,
        ContractSchemaVersion = "1",
    };

    private static ReceiptEnvelope Receipt(string requirementRef, VerificationDisposition disposition, string kind = ContractKinds.Acceptance, ContractAuthority authority = ContractAuthority.ServerPolicy) => new()
    {
        RequirementRef = requirementRef,
        AttemptId = Guid.NewGuid(),
        Kind = kind,
        Disposition = disposition,
        Authority = authority,
        ObservedAt = DateTimeOffset.UnixEpoch,
    };
}
