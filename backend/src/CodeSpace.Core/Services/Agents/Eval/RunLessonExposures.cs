using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Reads the exact lessons a run's model prompts actually saw across every prompt lane. These immutable receipts
/// are the only honest substrate for success attribution: the current lesson table can expire, consolidate, or
/// change after a run and therefore cannot reconstruct historical exposure.
/// </summary>
public static class RunLessonExposures
{
    public const string PlanAuthorLessonIdsOutputKey = "injectedLessonIds";

    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>> ReadAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return Empty;

        var receipts = await SupervisorExposuresAsync(db, runIds, teamId, cancellationToken).ConfigureAwait(false);
        var planner = await PlannerExposuresAsync(db, runIds, teamId, cancellationToken).ConfigureAwait(false);
        var assignments = await AssignmentExposuresAsync(db, runIds, teamId, cancellationToken).ConfigureAwait(false);
        var agents = await AgentExposuresAsync(db, runIds, teamId, cancellationToken).ConfigureAwait(false);

        foreach (var row in planner)
        {
            if (!Guid.TryParse(row.LessonId, out var lessonId)) continue;
            if (!receipts.TryGetValue(row.RunId, out var ids)) receipts[row.RunId] = ids = [];
            ids.Add(lessonId);
        }

        foreach (var row in agents)
        {
            if (!Guid.TryParse(row.LessonId, out var lessonId)) continue;
            if (!receipts.TryGetValue(row.RunId, out var ids)) receipts[row.RunId] = ids = [];
            ids.Add(lessonId);
        }

        foreach (var row in assignments)
        {
            if (!receipts.TryGetValue(row.RunId, out var ids)) receipts[row.RunId] = ids = [];
            ids.Add(row.LessonId);
        }

        return receipts
            .Where(pair => pair.Value.Count > 0)
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<Guid>)pair.Value.Distinct().OrderBy(id => id).ToList());
    }

    private static async Task<Dictionary<Guid, List<Guid>>> SupervisorExposuresAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(row => row.TeamId == teamId && runIds.Contains(row.SupervisorRunId) && row.LessonIds.Count > 0)
            .Select(row => new { row.SupervisorRunId, row.LessonIds })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(row => row.SupervisorRunId)
            .ToDictionary(group => group.Key, group => group.SelectMany(row => row.LessonIds).Distinct().ToList());
    }

    private static async Task<List<PlannerLessonRow>> PlannerExposuresAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        var ids = runIds.ToArray();

        return await db.Database.SqlQuery<PlannerLessonRow>($"""
            SELECT r.run_id AS run_id, exposure.value AS lesson_id
            FROM workflow_run_record r
            JOIN workflow_run w ON w.id = r.run_id
            CROSS JOIN LATERAL jsonb_array_elements_text(
                CASE WHEN jsonb_typeof(r.payload_json -> 'outputs' -> {PlanAuthorLessonIdsOutputKey}) = 'array'
                     THEN r.payload_json -> 'outputs' -> {PlanAuthorLessonIdsOutputKey}
                     ELSE '[]'::jsonb END) AS exposure(value)
            WHERE w.team_id = {teamId}
              AND r.run_id = ANY({ids})
              AND r.record_type = {WorkflowRunRecordTypes.NodeCompleted}
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record PlannerLessonRow(Guid RunId, string LessonId);

    private static async Task<List<AssignmentLessonRow>> AssignmentExposuresAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        var ids = runIds.ToArray();
        return await db.Database.SqlQuery<AssignmentLessonRow>($"""
            SELECT assignment.workflow_run_id AS run_id, exposure.lesson_id
            FROM workflow_run_lesson_assignment assignment
            CROSS JOIN LATERAL unnest(assignment.lesson_ids) AS exposure(lesson_id)
            WHERE assignment.team_id = {teamId} AND assignment.workflow_run_id = ANY({ids})
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record AssignmentLessonRow(Guid RunId, Guid LessonId);

    private static async Task<List<AgentLessonRow>> AgentExposuresAsync(CodeSpaceDbContext db, IReadOnlyList<Guid> runIds, Guid teamId, CancellationToken cancellationToken)
    {
        var ids = runIds.ToArray();
        return await db.Database.SqlQuery<AgentLessonRow>($"""
            SELECT a.workflow_run_id AS run_id, exposure.value AS lesson_id
            FROM agent_run a
            JOIN workflow_run w ON w.id = a.workflow_run_id
            CROSS JOIN LATERAL jsonb_array_elements_text(
                CASE WHEN jsonb_typeof(a.task_jsonb -> 'lessonIds') = 'array'
                     THEN a.task_jsonb -> 'lessonIds'
                     ELSE '[]'::jsonb END) AS exposure(value)
            WHERE a.team_id = {teamId}
              AND w.team_id = {teamId}
              AND a.workflow_run_id = ANY({ids})
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record AgentLessonRow(Guid RunId, string LessonId);

    private static readonly IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Empty = new Dictionary<Guid, IReadOnlyList<Guid>>();
}
