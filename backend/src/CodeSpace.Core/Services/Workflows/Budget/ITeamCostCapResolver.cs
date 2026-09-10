using CodeSpace.Messages.Budget;

namespace CodeSpace.Core.Services.Workflows.Budget;

/// <summary>
/// The EFFECTIVE cost cap for a team — its own <c>budget_team_cap</c> row, or the deployment-wide fallback standing
/// in for one, or nothing at all.
///
/// <para>Deliberately separate from <see cref="ITeamCostCapService"/> (which manages the row and reads its team
/// from <c>ICurrentTeam</c>): this side is on the admission hot path, which runs inside Hangfire workers and other
/// scopes with no HTTP request behind them, so it must not depend on request identity to answer.</para>
/// </summary>
public interface ITeamCostCapResolver
{
    /// <summary>The team's effective cap, or null when neither a team row nor a deployment fallback applies — the pre-5b-ii posture of a per-run ceiling only.</summary>
    Task<TeamCostCap?> ResolveAsync(Guid teamId, CancellationToken cancellationToken);
}
