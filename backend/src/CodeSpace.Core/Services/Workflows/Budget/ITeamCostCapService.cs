using CodeSpace.Messages.Budget;

namespace CodeSpace.Core.Services.Workflows.Budget;

/// <summary>Sets and clears the CURRENT team's standing cost cap. Team scope comes from <c>ICurrentTeam</c>, never a body — the same rule every other team-scoped write follows.</summary>
public interface ITeamCostCapService
{
    /// <summary>The current team's effective cap for display, including a deployment fallback standing in for a missing row.</summary>
    Task<TeamCostCap?> GetAsync(CancellationToken cancellationToken);

    /// <summary>Set (or replace) the current team's cap. Returns what is now in force.</summary>
    Task<TeamCostCap> SetAsync(decimal capUsd, CancellationToken cancellationToken);

    /// <summary>Remove the current team's cap row, dropping the team back to the deployment fallback. Idempotent.</summary>
    Task ClearAsync(CancellationToken cancellationToken);
}
