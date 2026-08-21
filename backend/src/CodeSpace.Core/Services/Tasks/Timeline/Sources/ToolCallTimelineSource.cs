using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Tasks.Timeline;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// The TOOL-CALL timeline source — it reads the side-effecting tool-call ledger (<c>tool_call_ledger</c>) for the run's
/// agent runs and projects each real side effect (a git.open_pr, a git.commit, a governed command) into a timeline
/// event tagged with its agent (and node). The agent runs are read TEAM-SCOPED (mirroring
/// <see cref="AgentEventTimelineSource"/>); the ledger read is team-scoped too (the row carries <c>TeamId</c>) and
/// EXCLUDES the cross-grain <c>decision.request</c> rows — those are DECISIONS the "Needs decision" queue surfaces,
/// not tool executions. The exclusion keys on <c>ToolKind == DecisionToolKinds.DecisionRequest</c> (the canonical
/// discriminator every other ledger consumer uses — both reapers, the decision queue, the answer resolver), NOT on the
/// <c>DecisionEnvelopeJson</c> being set: a decision row is INSERTed with a null envelope and only stashes it AFTER a
/// second write, so an envelope-null proxy would leak a phantom "Called decision.request" during that window (or forever,
/// after a crash between park and stash). Contributes nothing for a run whose agents made no side-effecting call
/// (a read-only / plain workflow run). READ-ONLY.
/// </summary>
public sealed class ToolCallTimelineSource : IRunTimelineSource, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public ToolCallTimelineSource(CodeSpaceDbContext db) { _db = db; }

    public string SourceKey => ToolCallTimelineMap.Key;

    public async Task<IReadOnlyList<RunTimelineEvent>> ContributeAsync(RunTimelineContext context, CancellationToken cancellationToken)
    {
        var calls = await ToolCallRowsQuery(_db, context.TeamId, context.RunId).ToListAsync(cancellationToken).ConfigureAwait(false);

        return calls.Select(ToolCallTimelineMap.ToEvent).ToList();
    }

    /// <summary>
    /// The run's SIDE-EFFECTING tool calls, team-scoped and chronological. The exactly-once receipt can be arbitrarily
    /// large and is load-bearing for replay, while the timeline displays only one best-effort human detail. PostgreSQL
    /// therefore extracts the small ordered field set and bounds it before bytes cross the process boundary. The raw
    /// result, decision envelope, approval bearer and idempotency authority never enter this observation projection.
    /// Internal so the translated SQL—not merely the result type—is test-pinned.
    /// </summary>
    internal static IQueryable<ToolCallTimelineRow> ToolCallRowsQuery(CodeSpaceDbContext db, Guid teamId, Guid workflowRunId) =>
        db.Database.SqlQuery<ToolCallTimelineRow>($$"""
            SELECT t.id AS id,
                   t.agent_run_id AS agent_run_id,
                   a.node_id AS node_id,
                   t.tool_kind AS tool_kind,
                   t.status AS status,
                   CASE WHEN char_length(t.error) > 512 THEN left(t.error, 512) || '…' ELSE t.error END AS error,
                   t.created_date AS created_date,
                   CASE WHEN char_length(detail.value) > 512 THEN left(detail.value, 512) || '…' ELSE detail.value END AS result_detail
            FROM tool_call_ledger AS t
            INNER JOIN agent_run AS a ON a.id = t.agent_run_id AND a.team_id = t.team_id
            LEFT JOIN LATERAL (
                SELECT CASE WHEN t.status = 'Succeeded' AND jsonb_typeof(t.result_jsonb) = 'object' THEN COALESCE(
                    CASE WHEN jsonb_typeof(t.result_jsonb -> 'summary') IN ('string', 'number') THEN nullif(btrim(t.result_jsonb ->> 'summary'), '') END,
                    CASE WHEN jsonb_typeof(t.result_jsonb -> 'message') IN ('string', 'number') THEN nullif(btrim(t.result_jsonb ->> 'message'), '') END,
                    CASE WHEN jsonb_typeof(t.result_jsonb -> 'html_url') IN ('string', 'number') THEN nullif(btrim(t.result_jsonb ->> 'html_url'), '') END,
                    CASE WHEN jsonb_typeof(t.result_jsonb -> 'url') IN ('string', 'number') THEN nullif(btrim(t.result_jsonb ->> 'url'), '') END,
                    CASE WHEN jsonb_typeof(t.result_jsonb -> 'number') IN ('string', 'number') AND nullif(btrim(t.result_jsonb ->> 'number'), '') IS NOT NULL THEN '#' || btrim(t.result_jsonb ->> 'number') END,
                    CASE WHEN jsonb_typeof(t.result_jsonb -> 'ref') IN ('string', 'number') THEN nullif(btrim(t.result_jsonb ->> 'ref'), '') END,
                    CASE WHEN jsonb_typeof(t.result_jsonb -> 'sha') IN ('string', 'number') THEN nullif(btrim(t.result_jsonb ->> 'sha'), '') END
                ) END AS value
            ) AS detail ON TRUE
            WHERE t.team_id = {{teamId}}
              AND a.workflow_run_id = {{workflowRunId}}
              AND t.tool_kind <> {{DecisionToolKinds.DecisionRequest}}
            ORDER BY t.created_date, t.id
            """);
}

/// <summary>Bounded, observation-only row used by the Workflow Run timeline; never an execution/replay carrier.</summary>
internal sealed class ToolCallTimelineRow
{
    public Guid Id { get; set; }
    public Guid AgentRunId { get; set; }
    public string? NodeId { get; set; }
    public string ToolKind { get; set; } = default!;
    public string Status { get; set; } = default!;
    public string? Error { get; set; }
    public string? ResultDetail { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
}
