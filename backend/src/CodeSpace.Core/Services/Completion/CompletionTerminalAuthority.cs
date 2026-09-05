using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Completion;

/// <summary>The authority's verdict at the terminal boundary: the status the run row gets, the parked/failed reason when the authority changed it, the decision it derived (null when the authority passed through), and the ledger watermarks the backing assessment READ (Lock Clause 2 — null on pass-through).</summary>
public sealed record TerminalArbitration(WorkflowRunStatus Status, string? Reason, TerminalDecision? Decision, CompletionLedgerWatermarks? Watermarks = null);

public interface ICompletionTerminalAuthority
{
    /// <summary>Arbitrate the engine's would-be terminal for this run. Anything but an Enforced-mode SUCCESS claim passes through verbatim.</summary>
    Task<TerminalArbitration> ArbitrateAsync(Guid workflowRunId, Guid teamId, string? enforcementMode, WorkflowRunStatus engineStatus, CancellationToken cancellationToken);

    /// <summary>P2b-4 (Lock Clause 2): whether the run's ledgers still match the watermarks the arbitration's assessment read — false means a late fact landed and the terminal must recompose or park, never stamp a stale claim.</summary>
    Task<bool> VerifyWatermarksAsync(Guid workflowRunId, Guid teamId, CompletionLedgerWatermarks captured, CancellationToken cancellationToken);
}

/// <summary>
/// P2b-1 (Lock Clause 1): the ONE production owner of the terminal SUCCESS claim. Active ONLY for a run whose
/// stamped <c>CompletionEnforcementMode</c> is <c>Enforced</c> — Legacy and Shadow runs pass through
/// byte-identically, and nothing stamps Enforced until a cohort qualifies on the accumulated
/// <c>would_be_terminal_decision</c> parity evidence. For an Enforced run claiming Success, the authority
/// composes the assessment AT the terminal boundary, probes handoff reachability, and maps the sealed
/// six-state decision onto the run vocabulary: CleanSuccess → Success (the only VDS-eligible state);
/// HonestFailure → Failure with the reason named; everything else (NeedsReview / NeedsClarification / Park /
/// Unsupported) → Suspended — parked for a human, never a fake Success and never a fake Failure. A compose
/// that cannot be derived fails CLOSED to parked. The engine's own Failure/Cancelled claims are already honest
/// non-successes and stand unchanged.
/// </summary>
public sealed class CompletionTerminalAuthority : ICompletionTerminalAuthority, IScopedDependency
{
    private readonly ICompletionAssessmentComposer _composer;
    private readonly ICompletionContractStore _contracts;
    private readonly ICompletionHandoffProbe _handoff;
    private readonly ICompletionCapabilityRegistry _capabilities;
    private readonly IModeProfileRegistry _modes;
    private readonly Persistence.Db.CodeSpaceDbContext _db;
    private readonly ILogger<CompletionTerminalAuthority> _logger;

    public CompletionTerminalAuthority(ICompletionAssessmentComposer composer, ICompletionContractStore contracts, ICompletionHandoffProbe handoff, ICompletionCapabilityRegistry capabilities, IModeProfileRegistry modes, Persistence.Db.CodeSpaceDbContext db, ILogger<CompletionTerminalAuthority> logger)
    {
        _composer = composer;
        _contracts = contracts;
        _handoff = handoff;
        _capabilities = capabilities;
        _modes = modes;
        _db = db;
        _logger = logger;
    }

    public async Task<bool> VerifyWatermarksAsync(Guid workflowRunId, Guid teamId, CompletionLedgerWatermarks captured, CancellationToken cancellationToken) =>
        captured == await CompletionLedgerWatermarks.CaptureAsync(_db, workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

    public async Task<TerminalArbitration> ArbitrateAsync(Guid workflowRunId, Guid teamId, string? enforcementMode, WorkflowRunStatus engineStatus, CancellationToken cancellationToken)
    {
        if (CompletionPolicy.ModeFor(enforcementMode) != CompletionEnforcementMode.Enforced || engineStatus != WorkflowRunStatus.Success)
            return new TerminalArbitration(engineStatus, Reason: null, Decision: null);

        // P2b-3 (Lock Clause 4): WHAT this run was asked for must be a REGISTERED capability — an ask outside the
        // closed vocabulary parks honestly as Unsupported, never a silent attempt at terminalizing Success.
        var requirements = await _contracts.ListRequirementsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);
        var capabilityKey = CompletionCapability.Derive(requirements);

        if (_capabilities.Resolve(capabilityKey) is null)
            return new TerminalArbitration(WorkflowRunStatus.Suspended, $"completion-authority: Unsupported — capability '{capabilityKey}' is not registered", TerminalDecision.Unsupported);

        // P4 (Lock Clause 4, first cell of the matrix): HOW this run operates must be a REGISTERED mode too — a
        // run whose operating shape has no declared conformance story (an arbitrary generic graph) parks
        // Unsupported instead of terminalizing a Success nothing ever qualified. Derived from the run's own
        // launch-stamped projection kind, else its frozen definition's node shape.
        var mode = await RunModeReader.DeriveAsync(_db, workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        if (_modes.Resolve(mode) is not { } profile)
            return new TerminalArbitration(WorkflowRunStatus.Suspended, $"completion-authority: Unsupported — mode '{mode}' has no registered conformance profile", TerminalDecision.Unsupported);

        // Q3 (cohort admission, arbitration side): the mode must HOLD Enforceable standing at arbitration time,
        // not merely at launch — a cohort demoted by a reviewed registry edit stops terminalizing IMMEDIATELY:
        // its in-flight Enforced rows park here until re-graduation. Structural like the two registration gates
        // above (recomputed from the registry on every arbitration, nothing baked into the shadow mirror). The
        // predicate is THE one CompletionPolicy stamps the default cohort by (C5) — one reading, so the launch
        // stamp and this gate can never draw different cohort lines.
        if (!CompletionPolicy.IsEnforceable(profile))
            return new TerminalArbitration(WorkflowRunStatus.Suspended, $"completion-authority: Unsupported — mode '{mode}' holds ProtocolReadiness.{profile.Readiness}, below the Enforceable standing the Enforced cohort requires", TerminalDecision.Unsupported);

        // Lock Clause 2: capture the ledgers' watermarks BEFORE composing — conservative direction: a fact that
        // lands mid-compose reads as moved at the terminal boundary and forces a recompose, never a stale stamp.
        var watermarks = await CompletionLedgerWatermarks.CaptureAsync(_db, workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        var composed = await _composer.ComposeAsync(workflowRunId, teamId, assumeTerminalStatus: WorkflowRunStatus.Success, cancellationToken).ConfigureAwait(false);

        if (composed is null)
        {
            _logger.LogError("Terminal authority could not compose run {RunId}; failing CLOSED to parked — an underivable assessment can never back a Success claim", workflowRunId);
            return new TerminalArbitration(WorkflowRunStatus.Suspended, "completion-authority: assessment underivable — parked for review", Decision: null);
        }

        var receipts = await _contracts.ListReceiptsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);
        var handoffReachable = await _handoff.IsHandoffReachableAsync(workflowRunId, teamId, receipts, cancellationToken).ConfigureAwait(false);
        var decision = TerminalDecider.Decide(composed.Assessment, handoffReachable);

        // Re-capture AFTER compose: the composer's own write-through bridges legitimately append receipts, and the
        // terminal verify must compare against the ledgers the DECISION was actually derived over.
        watermarks = await CompletionLedgerWatermarks.CaptureAsync(_db, workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        // P1 (fail-close): a CleanSuccess BUILT OVER integrity violations parks instead — an identity-less receipt
        // folded under Shadow tolerance, a ghost-attempt contract error, an unsupported requirement schema. Only
        // the SUCCESS claim is gated: an HonestFailure over tainted evidence stamps Failure unchanged (failure is
        // the conservative direction), and the park states already park.
        if (decision == TerminalDecision.CleanSuccess && CompletionIntegrity.Violations(composed.Rejections, composed.ContractErrors, requirements) is { Count: > 0 } violations)
        {
            _logger.LogWarning("Terminal authority refused a CleanSuccess for run {RunId} — {Count} integrity violation(s): {Violations}", workflowRunId, violations.Count, string.Join(" · ", violations));

            return new TerminalArbitration(WorkflowRunStatus.Suspended, $"completion-authority: Park — the Success claim rests on evidence with integrity violations: {string.Join("; ", violations)}", TerminalDecision.Park, watermarks);
        }

        // P4 (Lock Clause 4, the per-cell law): a CleanSuccess must also EVIDENCE every upstream stage the mode's
        // profile declares Required — an un-staked contract, a plan-less tape, an attempt-less run, or fresh work
        // nothing ever integrated parks naming the exact stage(s). The completion-side six stages are the
        // decision's own conjuncts; this gate closes the four the decider cannot see.
        if (decision == TerminalDecision.CleanSuccess && UpstreamStageTrace.MissingRequired(profile, composed.ExercisedUpstreamStages) is { Count: > 0 } missingStages)
        {
            _logger.LogWarning("Terminal authority refused a CleanSuccess for run {RunId} — mode '{Mode}' requires stage(s) with no evidence: {Stages}", workflowRunId, mode, string.Join(", ", missingStages));

            return new TerminalArbitration(WorkflowRunStatus.Suspended, $"completion-authority: Park — required stage(s) without evidence for mode '{mode}': {string.Join(", ", missingStages)}", TerminalDecision.Park, watermarks);
        }

        // A non-clean decision names its evidence: the staked obligations, the receipts that answered them (with
        // dispositions), and the admission rejections — the difference between "parked, go read the composer
        // source" and "parked, acceptance:s2 has no receipt". Park triage runs on these lines.
        if (decision != TerminalDecision.CleanSuccess)
        {
            _logger.LogWarning("Terminal arbitration for run {RunId}: staked=[{Staked}] receipts=[{Receipts}]",
                workflowRunId,
                string.Join(", ", requirements.Take(16).Select(r => $"{r.Kind}:{r.RequirementRef}@{r.Requiredness}")),
                string.Join(", ", receipts.Take(16).Select(r => $"{r.Kind}:{r.RequirementRef}={r.Disposition}(ev={(r.EvidenceRef is null ? "null" : "y")})")));

            if (composed.Rejections.Count > 0)
                _logger.LogWarning("Terminal arbitration for run {RunId} composed over {Count} admission rejection(s): {Rejections}",
                    workflowRunId, composed.Rejections.Count, string.Join(" · ", composed.Rejections.Take(8).Select(r => $"[{r.Code}] {r.Reason}")));
        }

        return decision switch
        {
            TerminalDecision.CleanSuccess => new TerminalArbitration(WorkflowRunStatus.Success, Reason: null, decision, watermarks),
            TerminalDecision.HonestFailure => new TerminalArbitration(WorkflowRunStatus.Failure, $"completion-authority: honest failure (outcome={composed.Assessment.Outcome}, verification={composed.Assessment.Verification}, artifact={composed.Assessment.Artifact})", decision, watermarks),
            _ => new TerminalArbitration(WorkflowRunStatus.Suspended, $"completion-authority: {decision} — parked for a human (outcome={composed.Assessment.Outcome}, verification={composed.Assessment.Verification}, artifact={composed.Assessment.Artifact}, delivery={composed.Assessment.Delivery}, execution={composed.Assessment.Execution}, handoffReachable={handoffReachable})", decision, watermarks),
        };
    }
}
