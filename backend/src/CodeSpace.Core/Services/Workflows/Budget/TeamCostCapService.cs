using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Budget;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Budget;

/// <summary>
/// Sets and clears the current team's cap row. The ROLE gate is the mediator's own
/// <c>IRequireTeamPermission</c> behaviour (<c>budget.manage</c>, Admin and up) — this service is the one place the
/// row is written, so every entry point that reaches it has already passed that gate.
/// </summary>
public sealed class TeamCostCapService : ITeamCostCapService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTeam _currentTeam;
    private readonly ITeamCostCapResolver _resolver;

    public TeamCostCapService(CodeSpaceDbContext db, ICurrentUser currentUser, ICurrentTeam currentTeam, ITeamCostCapResolver resolver)
    {
        _db = db;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
        _resolver = resolver;
    }

    public async Task<TeamCostCap?> GetAsync(CancellationToken cancellationToken) =>
        await _resolver.ResolveAsync(RequireTeam(), cancellationToken).ConfigureAwait(false);

    public async Task<TeamCostCap> SetAsync(decimal capUsd, CancellationToken cancellationToken)
    {
        if (capUsd <= 0) throw new ArgumentOutOfRangeException(nameof(capUsd), capUsd, "a team cost cap must be a positive amount in USD — clear the cap rather than setting it to zero");

        var teamId = RequireTeam();
        var existing = await _db.BudgetTeamCap.SingleOrDefaultAsync(cap => cap.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        if (existing is null) _db.BudgetTeamCap.Add(new BudgetTeamCap { TeamId = teamId, CapUsd = capUsd, CapWindow = TeamCostCap.RollingThirtyDays });
        else Rewrite(existing, capUsd);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new TeamCostCap(teamId, capUsd, TeamCostCap.RollingThirtyDays);
    }

    /// <summary>Deleted by statement rather than load-then-remove so clearing a cap the team never had is a silent no-op instead of a not-found.</summary>
    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        var teamId = RequireTeam();

        await _db.BudgetTeamCap.Where(cap => cap.TeamId == teamId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The window is rewritten too: a row persisted under an older window name must not survive a cap change and keep being summed over the window this build no longer understands.</summary>
    private static void Rewrite(BudgetTeamCap existing, decimal capUsd)
    {
        existing.CapUsd = capUsd;
        existing.CapWindow = TeamCostCap.RollingThirtyDays;
    }

    private Guid RequireTeam() => _currentTeam.Id ?? throw new TenantAccessDeniedException(_currentUser.Id, Guid.Empty, $"{HeaderCurrentTeam.HeaderName} header missing");
}
