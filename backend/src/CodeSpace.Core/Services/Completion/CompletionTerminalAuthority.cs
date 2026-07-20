using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Completion;

/// <summary>The authority's verdict at the terminal boundary: the status the run row gets, the parked/failed reason when the authority changed it, and the decision it derived (null when the authority passed through).</summary>
public sealed record TerminalArbitration(WorkflowRunStatus Status, string? Reason, TerminalDecision? Decision);

public interface ICompletionTerminalAuthority
{
    /// <summary>Arbitrate the engine's would-be terminal for this run. Anything but an Enforced-mode SUCCESS claim passes through verbatim.</summary>
    Task<TerminalArbitration> ArbitrateAsync(Guid workflowRunId, Guid teamId, string? enforcementMode, WorkflowRunStatus engineStatus, CancellationToken cancellationToken);
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
    private readonly ILogger<CompletionTerminalAuthority> _logger;

    public CompletionTerminalAuthority(ICompletionAssessmentComposer composer, ICompletionContractStore contracts, ICompletionHandoffProbe handoff, ILogger<CompletionTerminalAuthority> logger)
    {
        _composer = composer;
        _contracts = contracts;
        _handoff = handoff;
        _logger = logger;
    }

    public async Task<TerminalArbitration> ArbitrateAsync(Guid workflowRunId, Guid teamId, string? enforcementMode, WorkflowRunStatus engineStatus, CancellationToken cancellationToken)
    {
        if (CompletionPolicy.ModeFor(enforcementMode) != CompletionEnforcementMode.Enforced || engineStatus != WorkflowRunStatus.Success)
            return new TerminalArbitration(engineStatus, Reason: null, Decision: null);

        var composed = await _composer.ComposeAsync(workflowRunId, teamId, assumeTerminalStatus: WorkflowRunStatus.Success, cancellationToken).ConfigureAwait(false);

        if (composed is null)
        {
            _logger.LogError("Terminal authority could not compose run {RunId}; failing CLOSED to parked — an underivable assessment can never back a Success claim", workflowRunId);
            return new TerminalArbitration(WorkflowRunStatus.Suspended, "completion-authority: assessment underivable — parked for review", Decision: null);
        }

        var receipts = await _contracts.ListReceiptsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);
        var handoffReachable = await _handoff.IsHandoffReachableAsync(workflowRunId, teamId, receipts, cancellationToken).ConfigureAwait(false);
        var decision = TerminalDecider.Decide(composed.Assessment, handoffReachable);

        return decision switch
        {
            TerminalDecision.CleanSuccess => new TerminalArbitration(WorkflowRunStatus.Success, Reason: null, decision),
            TerminalDecision.HonestFailure => new TerminalArbitration(WorkflowRunStatus.Failure, $"completion-authority: honest failure (outcome={composed.Assessment.Outcome}, verification={composed.Assessment.Verification}, artifact={composed.Assessment.Artifact})", decision),
            _ => new TerminalArbitration(WorkflowRunStatus.Suspended, $"completion-authority: {decision} — parked for a human (delivery={composed.Assessment.Delivery}, handoffReachable={handoffReachable})", decision),
        };
    }
}
