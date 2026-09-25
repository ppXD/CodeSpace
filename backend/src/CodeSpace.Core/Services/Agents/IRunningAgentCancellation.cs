using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// <see cref="IAgentRunService.CancelRunningAsync"/> in its two halves, for a caller that must make the cancel visible
/// inside its own transaction but cannot run the cancel's side effects there. <see cref="CancelRunningRowAsync"/> is the
/// epoch-fenced Running → Cancelled CAS alone: it joins the caller's transaction and touches nothing but the run's row, so
/// whatever reads the run after that commit already sees the agent Cancelled. <see cref="FinishRunningCancelAsync"/> is
/// the rest — expire its decisions, settle its spend claims, revoke its brokered credential, close its harness execution,
/// kill its process — which reach other scopes and connections that would wait on the caller's uncommitted row lock, and
/// closes the run's decisions, which the run's own end locks before its row: taken while the caller still held the row,
/// that close would invert the order. So the caller runs it after commit, outside any transaction, as
/// <see cref="IAgentRunService.CancelRunningAsync"/> runs both halves. Continue's revive is the caller: the stopped
/// attempt's running agents are cancelled with the generation bump, and the stop's own teardown, landing later, simply
/// loses the CAS.
/// </summary>
public interface IRunningAgentCancellation
{
    /// <summary>The row half: Running → Cancelled at the epoch just read (bumping it), stamping <paramref name="reason"/>. False when the run is no longer Running at that epoch — already terminal, or never launched — and it is left alone.</summary>
    Task<bool> CancelRunningRowAsync(Guid runId, string reason, CancellationToken cancellationToken);

    /// <summary>The side-effect half, for a run whose row <see cref="CancelRunningRowAsync"/> flipped and whose flip has committed. Best-effort end to end, and a no-op for a run that is not Cancelled.</summary>
    Task FinishRunningCancelAsync(Guid runId, AgentRunAbandonCause cause, CancellationToken cancellationToken);
}
