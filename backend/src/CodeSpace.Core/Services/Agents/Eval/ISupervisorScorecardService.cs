using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Computes a <see cref="SupervisorScorecard"/> over a team's supervisor-run history — the cross-run roll-up plus
/// recent per-run scores that make the L4-core supervisor lane measurable instead of asserted (PR-E E6). The
/// supervisor-lane sibling of <see cref="IAgentRunScorecardService"/>. Team-scoped (multi-tenant isolation — only
/// the caller's supervisor runs are scored); optionally windowed to a time horizon so an operator can watch a trend.
/// Read-only: it never writes the ledger and runs no engine logic.
/// </summary>
public interface ISupervisorScorecardService
{
    Task<SupervisorScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken);
}
