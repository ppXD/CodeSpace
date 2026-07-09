using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>P4 — the premature-stop-rate report, team-scoped (Rule 7: a narrow contract so tests can mock it).</summary>
public interface IPrematureStopRateService
{
    /// <summary>Compute the report over the team's task runs (every projection kind), optionally windowed by <paramref name="since"/> on <c>CreatedDate</c>. Null <paramref name="since"/> = the team's full history.</summary>
    Task<PrematureStopRateReport> ComputeAsync(Guid teamId, DateTimeOffset? since, CancellationToken cancellationToken);
}
