using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Pins <see cref="RealModelDeliveryGateE2ETests.ParkedCardFault"/> — the REQUIRED delivery-gate arm's Phase-1
/// verdict — through its PURE seam, because nothing else can. The arm itself is <c>[Category=RealModel]</c> and
/// needs a live brain, so a defect in the verdict LOGIC only ever surfaces as a red REQUIRED gate on main, hours
/// after merge and indistinguishable from a real engine regression: run 33946934743 reported "zero publish
/// manifests were captured" over a run whose opener had read a real Agent-kind manifest, because the swallowed-
/// capture reading was not actually guarded by the "card does not name the conflict" condition it was written
/// under. These four cases cover the whole decision, so the arm's own honest arc (a patch-only card over captured
/// work ⇒ PROCEED to Phase 2) can never again be graded as a code fault.
///
/// <para>Pure logic — no Postgres, no fixture, no model. Carries the E2E lane's traits only so it RUNS: the
/// <c>Category=E2E&amp;Surface=Engine</c> gate is the one CI lane that executes this assembly.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Engine")]
public sealed class RealModelDeliveryGateVerdictTests
{
    /// <summary>The card <c>SupervisorDeliveryGate</c> mints when the publish reached a patch-only repository — the arc this arm exists to prove.</summary>
    private const string PatchOnlyCard = SupervisorDeliveryGate.QuestionPrefix
        + "the required pull request was skipped by policy (primary: the repository requires patch-only publishing) — change the publish mode there and answer to re-attempt once; if still blocked, the run completes without the pull request";

    /// <summary>The gate's OTHER zero-PR card — honest only over a run that captured nothing.</summary>
    private const string EmptyPublishCard = SupervisorDeliveryGate.QuestionPrefix
        + "the delivery contract requires a pull request, but the publish attempt found no published branch to open one from — answering re-attempts the publish once; if there is still nothing to open, the run completes without it";

    [Theory]
    [InlineData(1, true)]     // the honest arc: agents captured branchless patch-only work, the card names the conflict
    [InlineData(0, true)]     // a card naming the conflict is never re-graded by the ledger — the wording IS the fact
    [InlineData(0, false)]
    public void A_card_that_names_the_patch_only_conflict_lets_the_arc_proceed(int agentManifestCount, bool anyAgentShowsWork)
    {
        RealModelDeliveryGateE2ETests.ParkedCardFault(PatchOnlyCard, agentManifestCount, anyAgentShowsWork)
            .ShouldBeNull("the gate named the required-PR × patch-only conflict — Phase 1 succeeded and the adjudication phase must be REACHED, not short-circuited into a fault");
    }

    [Fact]
    public void A_mis_named_card_over_captured_work_is_the_code_fault()
    {
        var fault = RealModelDeliveryGateE2ETests.ParkedCardFault(EmptyPublishCard, agentManifestCount: 1, anyAgentShowsWork: true);

        fault!.Value.Outcome.ShouldBe(RealModelOutcome.CodeFault);
        fault.Value.Note.ShouldContain("does not name the patch-only policy conflict", customMessage: "the ledger HAS the evidence the card denies — that mismatch is the fault being reported");
    }

    [Fact]
    public void A_mis_named_card_whose_tape_shows_work_reports_the_swallowed_capture()
    {
        var fault = RealModelDeliveryGateE2ETests.ParkedCardFault(EmptyPublishCard, agentManifestCount: 0, anyAgentShowsWork: true);

        fault!.Value.Outcome.ShouldBe(RealModelOutcome.CodeFault);
        fault.Value.Note.ShouldContain("swallowed it", customMessage: "the tape says the agents worked and the ledger recorded nothing — the capture pipeline, not the model");
    }

    [Fact]
    public void A_mis_named_card_with_no_work_anywhere_is_the_models_miss_and_never_gates()
    {
        var fault = RealModelDeliveryGateE2ETests.ParkedCardFault(EmptyPublishCard, agentManifestCount: 0, anyAgentShowsWork: false);

        fault!.Value.Outcome.ShouldBe(RealModelOutcome.CapabilityMiss, "a live model that produced no diff is a capability miss — main must never red for it");
    }
}
