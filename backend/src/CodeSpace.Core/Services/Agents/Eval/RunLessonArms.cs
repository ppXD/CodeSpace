using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Reads the Arc-D lesson A/B arm a run ran under, off its supervisor decision rows. ONE implementation shared by
/// the durable <see cref="IRunScorecardWriter"/> (per run) and the live rollup's by-arm slice (batched over a
/// window) — the same shape <see cref="UnattendedDeliveryScorecardService.DegradedStopRunIdsAsync"/> already uses
/// so a cross-service read never forks into two subtly different answers.
///
/// <para>The arm is a JOURNAL field frozen at insert and re-frozen from the run's earliest row on every later turn
/// (<c>SupervisorTurnService.ResolveLessonInjectionAsync</c>), so a run has exactly one arm and the earliest row is
/// authoritative. A run absent from the result has no decision ledger at all — a single-agent or plan-map run,
/// which is UNMEASURED, not the <c>none</c> control.</para>
/// </summary>
public static class RunLessonArms
{
    public static async Task<IReadOnlyDictionary<Guid, string>> ReadAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return Empty;

        var rows = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.TeamId == teamId && runIds.Contains(d.SupervisorRunId) && d.LessonArm != null)
            .Select(d => new { d.SupervisorRunId, d.LessonArm, d.Sequence })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.SupervisorRunId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Sequence).First().LessonArm!);
    }

    private static readonly IReadOnlyDictionary<Guid, string> Empty = new Dictionary<Guid, string>();
}
