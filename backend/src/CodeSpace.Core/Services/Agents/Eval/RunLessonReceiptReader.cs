using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Learning;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>An exact ordered prompt receipt recovered during rollout before the canonical run assignment exists.</summary>
public sealed record RunLessonReceipt(string Arm, IReadOnlyList<Guid> LessonIds);

/// <summary>Recovers one authoritative pre-assignment receipt without unioning distinct prompt histories.</summary>
public static class RunLessonReceiptReader
{
    public static async Task<RunLessonReceipt?> ReadAsync(CodeSpaceDbContext db, Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var supervisor = await db.SupervisorDecisionRecord.AsNoTracking()
            .Where(row => row.TeamId == teamId && row.SupervisorRunId == runId && (row.LessonArm == LessonArms.Injected || row.LessonArm == LessonArms.Withheld || row.LessonArm == LessonArms.None))
            .OrderBy(row => row.Sequence)
            .Select(row => new { row.LessonArm, row.LessonIds })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (supervisor is not null) return Create(supervisor.LessonArm!, supervisor.LessonIds);

        var planner = await db.Database.SqlQuery<JsonReceiptRow>($"""
            SELECT r.payload_json -> 'outputs' ->> {RunLessonArms.PlanAuthorArmOutputKey} AS arm,
                   COALESCE(r.payload_json -> 'outputs' -> {RunLessonExposures.PlanAuthorLessonIdsOutputKey}, '[]'::jsonb)::text AS lesson_ids_json
            FROM workflow_run_record r
            JOIN workflow_run w ON w.id = r.run_id
            WHERE r.run_id = {runId} AND w.team_id = {teamId}
              AND r.record_type = {WorkflowRunRecordTypes.NodeCompleted}
              AND r.payload_json -> 'outputs' ->> {RunLessonArms.PlanAuthorArmOutputKey} IN ('injected', 'withheld', 'none')
            ORDER BY r.sequence
            LIMIT 1
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (planner is not null) return Create(planner.Arm, ParseIds(planner.LessonIdsJson));

        var agent = await db.Database.SqlQuery<JsonReceiptRow>($"""
            SELECT a.task_jsonb ->> 'lessonArm' AS arm,
                   COALESCE(a.task_jsonb -> 'lessonIds', '[]'::jsonb)::text AS lesson_ids_json
            FROM agent_run a
            JOIN workflow_run w ON w.id = a.workflow_run_id
            WHERE a.workflow_run_id = {runId} AND a.team_id = {teamId} AND w.team_id = {teamId}
              AND a.task_jsonb ->> 'lessonArm' IN ('injected', 'withheld', 'none')
            ORDER BY a.created_date, a.id
            LIMIT 1
            """).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return agent is null ? null : Create(agent.Arm, ParseIds(agent.LessonIdsJson));
    }

    private static RunLessonReceipt Create(string arm, IReadOnlyList<Guid> lessonIds) => new(arm, arm == LessonArms.Injected ? lessonIds.Distinct().ToList() : []);

    private static IReadOnlyList<Guid> ParseIds(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out _)).Select(value => Guid.Parse(value.GetString()!)).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed record JsonReceiptRow(string Arm, string LessonIdsJson);
}
