using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Budget;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Budget;

/// <summary>
/// Reads the team's cap row, and falls back to the deployment cap when it has none.
///
/// <para>Order matters and is one-directional: a team row WINS over the deployment value rather than being clamped
/// by it. The deployment cap is a floor under teams nobody has configured — an operator who raises one team above
/// it has said so explicitly, and silently clamping that would make the management endpoint a lie.</para>
///
/// <para><c>AsNoTracking</c> — this row is read, never mutated — but still resolved inside the ledger's admission
/// transaction on the SAME scoped <see cref="CodeSpaceDbContext"/>, so it sees that transaction's own writes and
/// holds its locks regardless of tracking.</para>
/// </summary>
public sealed class TeamCostCapResolver : ITeamCostCapResolver, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public TeamCostCapResolver(CodeSpaceDbContext db) => _db = db;

    public async Task<TeamCostCap?> ResolveAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var row = await _db.BudgetTeamCap.AsNoTracking().SingleOrDefaultAsync(cap => cap.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        if (row is not null) return new TeamCostCap(teamId, row.CapUsd, row.CapWindow);

        if (RuntimeSettings.Current.DeploymentCostCapUsd is not { } fallback) return null;

        return new TeamCostCap(teamId, fallback, TeamCostCap.RollingThirtyDays) { Grain = BudgetCapGrain.Deployment };
    }
}
