using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: B2 (amend-acceptance arc) — the explicit verdict state model. WAIVED ≠ PASSED at every objective-truth
/// read: the shared withhold predicate treats a waived unit exactly like a rejected one at the head doors, the
/// run-wide withheld aggregate folds it, a waived unit mints a Waived receipt under Operator authority (the
/// completion kernel's existing Waived semantics then apply — Abstained, never Solved), the scorecard's oracle leg
/// never reads a waived-only run Solved, and the wire field is null-omitted so every pre-B2 row serializes
/// byte-identical. Nothing WRITES Waived until the co-sign overlay (B3) — these pins make that write land on
/// ready, non-launderable plumbing.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorWaivedVerdictTests
{
    private static SupervisorAgentResult Unit(bool? passed = null, VerificationDisposition? verdict = null, string status = "Succeeded") =>
        new() { AgentRunId = Guid.NewGuid(), Status = status, ProducedBranch = "codespace/agent/x", AcceptancePassed = passed, AcceptanceVerdict = verdict };

    // ── the shared withhold predicate ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(false, null, true)]                              // rejected → withheld (pre-B2 behaviour)
    [InlineData(true, null, false)]                              // passed → integrates
    [InlineData(null, null, false)]                              // ungraded → integrates (byte-identical to pre-slice)
    [InlineData(null, VerificationDisposition.Waived, true)]     // waived → withheld (B2: WAIVED ≠ PASSED)
    [InlineData(true, VerificationDisposition.Waived, true)]     // defensive: a waive dominates even a stray pass bool
    public void Withheld_from_head_is_rejected_or_waived(bool? passed, VerificationDisposition? verdict, bool expected)
    {
        SupervisorOutcome.IsWithheldFromHead(Unit(passed, verdict)).ShouldBe(expected);
    }

    [Fact]
    public void The_run_wide_withheld_aggregate_folds_waived_and_rejected_alike()
    {
        var rejected = Unit(passed: false);
        var waived = Unit(verdict: VerificationDisposition.Waived);
        var passed = Unit(passed: true);

        var decision = Staging(rejected, waived, passed);

        var withheld = SupervisorOutcome.WithheldAgentRunIds(new[] { decision });

        withheld.ShouldBe(new HashSet<Guid> { rejected.AgentRunId, waived.AgentRunId },
            "the DC-3 ledger-direct resolver and the DC-2b auto-PR floor both read this set — a waived unit must never auto-publish");
    }

    // ── wire compatibility ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_absent_verdict_serializes_byte_identical_and_waived_round_trips()
    {
        JsonSerializer.Serialize(Unit(passed: true), AgentJson.Options)
            .ShouldNotContain("acceptanceVerdict", customMessage: "null-omitted — every pre-B2 row's bytes are unchanged");

        var json = JsonSerializer.Serialize(Unit(verdict: VerificationDisposition.Waived), AgentJson.Options);
        json.ShouldContain("\"acceptanceVerdict\":\"waived\"", customMessage: "string-enum on the wire, Contradiction-field precedent");
        JsonSerializer.Deserialize<SupervisorAgentResult>(json, AgentJson.Options)!.AcceptanceVerdict.ShouldBe(VerificationDisposition.Waived);
    }

    // ── the completion story: a waived unit attests as Waived ─────────────────────────────────────────

    [Fact]
    public void A_waived_unit_mints_a_waived_receipt_under_operator_authority()
    {
        var waived = Unit(verdict: VerificationDisposition.Waived);

        var receipts = SupervisorGradedReceipts.FromTape(new[] { Staging(waived, subtaskIds: new[] { "s1" }) });

        var receipt = receipts.ShouldHaveSingleItem("a waived unit must not VANISH from the completion story — zero waive trace was the B0 hole");
        receipt.Disposition.ShouldBe(VerificationDisposition.Waived, "the kernel reads Waived → Abstained/WaivedByPolicy — never Solved, never Delivered");
        receipt.Authority.ShouldBe(ContractAuthority.Operator, "a human co-signed the forgo-verification; the server graded nothing, so ServerPolicy would lie");
        receipt.RequirementRef.ShouldBe("acceptance:s1");
    }

    [Fact]
    public void An_ungraded_unwaived_unit_still_mints_no_receipt()
    {
        SupervisorGradedReceipts.FromTape(new[] { Staging(Unit(), subtaskIds: new[] { "s1" }) })
            .ShouldBeEmpty("byte-identical to pre-B2 — no verdict, no attestation");
    }

    // ── the scorecard's oracle leg ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_waived_manifest_never_reads_solved_via_either_leg()
    {
        UnattendedDeliveryScorecardService.IsSolved(new[] { Manifest(PublishAcceptanceState.Waived) }, WorkflowRunStatus.Success, degradedStop: false)
            .ShouldBeFalse("a fully-waived Success run counted Solved via the status fallback with zero waive trace — the exact B0 hole");

        UnattendedDeliveryScorecardService.IsSolved(new[] { Manifest(PublishAcceptanceState.Waived), Manifest(PublishAcceptanceState.Passed) }, WorkflowRunStatus.Success, degradedStop: false)
            .ShouldBeFalse("mixed passed+waived is not fully verified — mirrors CompletionReducer's severity order (Failed > Waived > Passed)");

        UnattendedDeliveryScorecardService.IsSolved(new[] { Manifest(PublishAcceptanceState.Passed) }, WorkflowRunStatus.Success, degradedStop: false)
            .ShouldBeTrue("regression pin — an all-passed run still solves");
    }

    private static PublishManifest Manifest(PublishAcceptanceState state) =>
        new() { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Kind = PublishManifestKind.Agent, RepositoryAlias = "primary", AcceptanceState = state, PublishStateValue = PublishState.Pushed };

    private static SupervisorPriorDecision Staging(params SupervisorAgentResult[] units) => Staging(units, subtaskIds: null);

    private static SupervisorPriorDecision Staging(SupervisorAgentResult[] units, string[]? subtaskIds)
    {
        var ids = units.Select(u => u.AgentRunId).ToArray();
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = ids, agentCount = ids.Length }, AgentJson.Options), units);
        var payload = subtaskIds is null ? "{}" : JsonSerializer.Serialize(new { subtaskIds }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payload, OutcomeJson = outcome };
    }

    private static SupervisorPriorDecision Staging(SupervisorAgentResult unit, string[] subtaskIds) => Staging(new[] { unit }, subtaskIds);
}
