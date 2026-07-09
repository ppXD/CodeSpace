using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Computes an <see cref="UnattendedDeliveryScorecard"/> over a team's run history — the path-to-intelligence
/// north-star metric ("task in → merged/published artifact out with zero human touches"), measured over EVERY
/// terminal run (single-agent or supervisor-orchestrated alike). Team-scoped (multi-tenant isolation); optionally
/// windowed to a time horizon so an operator can watch the trend. Read-only: it never writes the ledger and runs
/// no engine logic.
/// </summary>
public interface IUnattendedDeliveryScorecardService
{
    Task<UnattendedDeliveryScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken);
}
