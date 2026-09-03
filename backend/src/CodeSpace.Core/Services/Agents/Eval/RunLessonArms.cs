using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Reads the Arc-D lesson A/B arm a run ran under. ONE implementation shared by the durable
/// <see cref="IRunScorecardWriter"/> (per run) and the live rollup's by-arm slice (batched over a window) — the
/// same shape <see cref="UnattendedDeliveryScorecardService.DegradedStopRunIdsAsync"/> already uses, so a
/// cross-service read never forks into two subtly different answers.
///
/// <para>BOTH lanes assign an arm, so both are read. The SUPERVISOR lane freezes it on every decision row
/// (<c>SupervisorTurnService.ResolveLessonInjectionAsync</c> re-freezes from the run's earliest row, so a run has
/// exactly one arm and the earliest row is authoritative). The PLANNER lane stamps it on the authored plan, which
/// reaches the ledger as the <c>plan.author</c> node's own small <c>lessonArm</c> output key. Reading only the
/// supervisor ledger reported a TREATED planner run as unmeasured — the arm was assigned, the lessons were
/// injected into the plan prompt, and the rollup counted it outside the experiment.</para>
///
/// <para>A run absent from the result was in neither lane — a single-agent run, which is UNMEASURED, not the
/// <c>none</c> control.</para>
/// </summary>
public static class RunLessonArms
{
    /// <summary>
    /// The <c>plan.author</c> output key carrying the arm. It is its OWN key rather than a field inside that node's
    /// <c>json</c> output because <c>json</c> is offloaded to the artifact store once a plan is large — reading the
    /// arm from there would report <c>unmeasured</c> for exactly the big plans whose arm matters most. Mirrors the
    /// <c>authoredByModel</c> key, promoted for the identical reason. Pinned by a unit test.
    /// </summary>
    public const string PlanAuthorArmOutputKey = "lessonArm";

    public static async Task<IReadOnlyDictionary<Guid, string>> ReadAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return Empty;

        var arms = await SupervisorArmsAsync(db, runIds, teamId, cancellationToken).ConfigureAwait(false);
        var planner = await PlannerArmsAsync(db, runIds, teamId, cancellationToken).ConfigureAwait(false);

        // The supervisor's frozen arm WINS where a run has both: it is the arm the run's own decisions were taken
        // under, whereas a plan.author node inside that run records the arm its PLAN was authored under. The two
        // agree by construction (both hash team + the undecorated goal — LessonArmAgreementTests pins that), so
        // this only decides the tie-break, never a disagreement.
        foreach (var (runId, arm) in planner)
            if (!arms.ContainsKey(runId)) arms[runId] = arm;

        return arms;
    }

    /// <summary>The supervisor lane: the arm frozen on the run's EARLIEST decision row.</summary>
    private static async Task<Dictionary<Guid, string>> SupervisorArmsAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.TeamId == teamId && runIds.Contains(d.SupervisorRunId) && d.LessonArm != null)
            .Select(d => new { d.SupervisorRunId, d.LessonArm, d.Sequence })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.SupervisorRunId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Sequence).First().LessonArm!);
    }

    /// <summary>
    /// The planner lane: the arm on the run's FIRST <c>plan.author</c> <c>node.completed</c> record — the plan the
    /// run actually executed against. A re-plan (the edit loop) appends a later record under the same arm, since
    /// the assignment hashes the run's undecorated goal, which a re-plan's feedback fold deliberately does not move.
    ///
    /// <para>Extracted SERVER-SIDE with the jsonb path operator rather than by pulling payloads into memory and
    /// parsing: <c>node.completed</c> payloads carry every node's whole output map, so a 100-run window would drag
    /// megabytes across the wire to read one short string per run. The projection returns exactly the arm.</para>
    ///
    /// <para>Tenancy is a JOIN on the run rather than a trusted caller argument (<c>WorkflowRunRecord</c> carries no
    /// team of its own), so a borrowed team id reads nothing.</para>
    /// </summary>
    private static async Task<Dictionary<Guid, string>> PlannerArmsAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        var ids = runIds.ToArray();

        var rows = await db.Database.SqlQuery<PlannerArmRow>($"""
            SELECT r.run_id AS run_id,
                   r.sequence AS sequence,
                   r.payload_json -> 'outputs' ->> {PlanAuthorArmOutputKey} AS arm
            FROM workflow_run_record r
            JOIN workflow_run w ON w.id = r.run_id
            WHERE w.team_id = {teamId}
              AND r.run_id = ANY({ids})
              AND r.record_type = {WorkflowRunRecordTypes.NodeCompleted}
              AND r.payload_json -> 'outputs' ->> {PlanAuthorArmOutputKey} IS NOT NULL
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.RunId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Sequence).First().Arm);
    }

    /// <summary>One run's plan-authored arm as projected by the jsonb path query above.</summary>
    private sealed record PlannerArmRow(Guid RunId, long Sequence, string Arm);

    private static readonly IReadOnlyDictionary<Guid, string> Empty = new Dictionary<Guid, string>();
}
