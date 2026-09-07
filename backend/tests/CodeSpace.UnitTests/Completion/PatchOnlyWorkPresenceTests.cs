using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Completion;

/// <summary>
/// 🟢 Unit: under a PATCH-ONLY publish policy nothing is ever pushed, so "did this attempt produce work?" cannot be
/// answered by <c>ProducedBranch</c> alone — and for a while it was answered two different ways at once. The decider
/// asked <see cref="SupervisorOutcome.ResultShowsWork"/> (git ground truth: changed files, a pushed branch, or
/// either on any repo of a multi-repo workspace), read the unit's <c>no-branch-or-repo</c> grade as INFRA and told
/// the model "acceptance UNVERIFIED"; the two RECEIPT-minting sites asked <c>!IsNullOrEmpty(ProducedBranch)</c> —
/// which patch-only never sets — and wrote the SAME grade into the completion ledger as a hard
/// <see cref="VerificationDisposition.Failed"/>.
///
/// <para>Real-model main run 34068400279 is the receipt: <c>acceptance:acceptance:s3=Failed(ev=null)</c> on a unit
/// whose agent had really written files, folding to Verification=Failed → Outcome=Unsolved →
/// <c>completion-authority: honest failure</c> over an arc that had been human-adjudicated to completion.</para>
///
/// <para>These pin the closure: ONE definition (<see cref="AgentWorkPresence"/>) read by every site that classifies
/// a <c>no-branch-or-repo</c> grade — including the baseline-capture spend gate, whose disagreement cost a wasted
/// clone + judge call rather than a wrong verdict — so no publish policy can make the model's verdict and the
/// ledger's verdict disagree about the same unit again. The
/// work-FREE case is pinned alongside — a unit that genuinely produced nothing still grades <c>Failed</c>, because
/// there the fix IS to do the work, which an agent pass can do.</para>
/// </summary>
[Trait("Category", "Unit")]
public class PatchOnlyWorkPresenceTests
{
    /// <summary>The grade a publish that had nothing to push mints — infra ONLY when work exists (the publish failed), genuine when it does not (the agent produced nothing).</summary>
    private const string PatchOnlyGrade = "no-branch-or-repo";

    // ── One definition of "work exists", both result shapes ────────────────────────────────────────────

    [Theory]
    [InlineData(true, null, false, false, true)]     // patch-only: files changed, nothing ever pushed
    [InlineData(false, "codespace/agent/x", false, false, true)]
    [InlineData(false, null, true, false, true)]     // multi-repo: a sibling repo pushed
    [InlineData(false, null, false, true, true)]     // multi-repo patch-only: a sibling repo changed files
    [InlineData(false, null, false, false, false)]   // nothing anywhere — the only honest "no work"
    public void Both_result_shapes_read_the_same_work_present_fact(bool changedFiles, string? producedBranch, bool repoBranch, bool repoFiles, bool expected)
    {
        var repos = repoBranch || repoFiles
            ? new[] { new RepositoryRunResult { Alias = "web", ProducedBranch = repoBranch ? "codespace/agent/web" : null, ChangedFiles = repoFiles ? new[] { "web/a.ts" } : Array.Empty<string>() } }
            : Array.Empty<RepositoryRunResult>();

        AgentWorkPresence.ShowsWork(Raw(changedFiles, producedBranch, repos)).ShouldBe(expected, "the durable AgentRunResult the composer's workflow-agents lane reads");
        AgentWorkPresence.ShowsWork(Compact(changedFiles, producedBranch, repos)).ShouldBe(expected, "the tape compact the supervisor lane reads");
        SupervisorOutcome.ResultShowsWork(Compact(changedFiles, producedBranch, repos)).ShouldBe(expected, "the supervisor-side name the decider calls must stay the SAME fact");
    }

    // ── The four readers of that fact classify one fixture identically ─────────────────────────────────

    [Theory]
    [InlineData(true, VerificationDisposition.InfraUnknown)]   // the patch-only unit: work exists, the publish is what failed
    [InlineData(false, VerificationDisposition.Failed)]        // regression pin: no work anywhere is a genuine miss
    public void The_four_no_branch_or_repo_readers_agree_on_one_fixture(bool workProduced, VerificationDisposition expected)
    {
        var compact = Compact(changedFiles: workProduced, producedBranch: null, Array.Empty<RepositoryRunResult>()) with { AcceptancePassed = false, AcceptanceDetail = PatchOnlyGrade };
        var raw = Raw(changedFiles: workProduced, producedBranch: null, Array.Empty<RepositoryRunResult>()) with { AcceptancePassed = false, AcceptanceDetail = PatchOnlyGrade };

        var expectInfra = expected == VerificationDisposition.InfraUnknown;

        // 1. The DECIDER's read (LlmSupervisorDecider → SupervisorOutcome.ResultShowsWork → IsInfraFailure): what
        //    the model is told about its own unit — "acceptance UNVERIFIED" rather than "acceptance FAILED".
        AgentAcceptanceContract.IsInfraFailure(PatchOnlyGrade, SupervisorOutcome.ResultShowsWork(compact))
            .ShouldBe(expectInfra, "the verdict line the model reads");

        // 2. The SUPERVISOR TAPE's receipt (SupervisorGradedReceipts.FromTape → the completion ledger).
        TapeReceipt(compact).Disposition
            .ShouldBe(expected, "the tape's receipt must carry the SAME verdict the decider recited — this is the row that read Failed on run 34068400279");

        // 3. The WORKFLOW-AGENTS lane's receipt (CompletionAssessmentComposer's write-through bridge). Mirrors that
        //    site's one line; the real composer is driven end-to-end in SingleAgentSpineFlowTests.
        VerificationDispositions.Classify(raw.AcceptancePassed, raw.AcceptanceDetail, AgentWorkPresence.ShowsWork(raw))
            .ShouldBe(expected, "the single-agent lane mints its receipts off the raw result — same fact, same verdict");

        // 4. The BASELINE-CAPTURE spend gate — the VERY predicate SupervisorTurnService.Rehydrate's
        //    CaptureUnitBaselineAsync is gated on, not a mirror of it: "did this unit's candidate grade actually
        //    RUN?" decides whether a differential baseline is worth a second full clone + a second judge call. It
        //    read the SAME grade as GENUINE while the decider read it INFRA — the identical split-brain as the
        //    receipt above, paid in spend rather than in a false Failed: a baseline measured against a candidate
        //    grade that never ran can compare nothing.
        SupervisorOutcome.CandidateGradeRan(compact, new BenchmarkGrade { Passed = false, Detail = PatchOnlyGrade })
            .ShouldBe(!expectInfra, "the differential's spend gate must agree with the receipt the very same unit mints");
    }

    // ── What an InfraUnknown acceptance does downstream ────────────────────────────────────────────────

    [Fact]
    public void An_unverified_acceptance_parks_and_stops_being_a_false_failure()
    {
        var requirements = new[] { Requirement("acceptance:s3") };
        var facts = Facts(WorkflowRunStatus.Success);

        var unverified = CompletionReducer.Reduce(requirements, new[] { TapeReceipt(PatchOnly(workProduced: true)) }, facts);

        unverified.Verification.ShouldBe(VerificationDisposition.InfraUnknown, "the CHECK could not run — the candidate is unmeasured, not condemned");
        unverified.Outcome.ShouldBe(OutcomeDisposition.Unknown, "an unrunnable oracle states no truth about the candidate in EITHER direction");

        TerminalDecider.Decide(unverified, handoffReachable: true)
            .ShouldBe(TerminalDecision.NeedsReview, "UNVERIFIED PARKS: the fix must not turn a false Failed into a false Success — InfraUnknown reaches neither");

        TerminalDecider.IsVdsEligible(TerminalDecider.Decide(unverified, handoffReachable: true))
            .ShouldBeFalse("nothing but CleanSuccess may enter the north-star count");
    }

    [Fact]
    public void A_unit_that_produced_nothing_still_fails_honestly()
    {
        var requirements = new[] { Requirement("acceptance:s3") };

        var failed = CompletionReducer.Reduce(requirements, new[] { TapeReceipt(PatchOnly(workProduced: false)) }, Facts(WorkflowRunStatus.Success));

        failed.Verification.ShouldBe(VerificationDisposition.Failed);
        failed.Outcome.ShouldBe(OutcomeDisposition.Unsolved);

        TerminalDecider.Decide(failed, handoffReachable: true)
            .ShouldBe(TerminalDecision.HonestFailure, "regression pin — widening infra to every no-branch-or-repo grade would launder a real miss into a park");
    }

    // ── Builders ───────────────────────────────────────────────────────────────────────────────────────

    private static SupervisorAgentResult PatchOnly(bool workProduced) =>
        Compact(changedFiles: workProduced, producedBranch: null, Array.Empty<RepositoryRunResult>()) with { AcceptancePassed = false, AcceptanceDetail = PatchOnlyGrade };

    private static AgentRunResult Raw(bool changedFiles, string? producedBranch, IReadOnlyList<RepositoryRunResult> repos) => new()
    {
        Status = AgentRunStatus.Succeeded,
        ExitReason = "completed",
        ChangedFiles = changedFiles ? new[] { "src/a.cs" } : Array.Empty<string>(),
        ProducedBranch = producedBranch,
        RepositoryResults = repos,
    };

    private static SupervisorAgentResult Compact(bool changedFiles, string? producedBranch, IReadOnlyList<RepositoryRunResult> repos) => new()
    {
        AgentRunId = Guid.NewGuid(),
        Status = nameof(AgentRunStatus.Succeeded),
        ChangedFiles = changedFiles ? new[] { "src/a.cs" } : Array.Empty<string>(),
        ProducedBranch = producedBranch,
        RepositoryResults = repos,
    };

    /// <summary>The receipt the supervisor tape mints for one graded unit — through the real <see cref="SupervisorGradedReceipts.FromTape"/>, off a real folded spawn decision.</summary>
    private static ReceiptEnvelope TapeReceipt(SupervisorAgentResult unit)
    {
        var outcome = SupervisorOutcome.FoldAgentResults(
            JsonSerializer.Serialize(new { agentRunIds = new[] { unit.AgentRunId }, agentCount = 1 }, AgentJson.Options), new[] { unit });

        var decision = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.Spawn,
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = JsonSerializer.Serialize(new { subtaskIds = new[] { "s3" } }, AgentJson.Options),
            OutcomeJson = outcome,
        };

        return SupervisorGradedReceipts.FromTape(new[] { decision }).ShouldHaveSingleItem();
    }

    private static CompletionRunFacts Facts(WorkflowRunStatus status) => new() { TerminalStatus = status, HadOrderlyTerminal = true };

    private static RequirementEnvelope Requirement(string requirementRef) => new()
    {
        RequirementRef = requirementRef,
        Kind = ContractKinds.Acceptance,
        Requiredness = Requiredness.Required,
        Authority = ContractAuthority.Operator,
        ContractSchemaVersion = "1",
    };
}
