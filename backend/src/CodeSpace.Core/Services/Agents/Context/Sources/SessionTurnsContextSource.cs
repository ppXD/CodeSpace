using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CodeSpace.Core.Services.Agents.Context.Sources;

/// <summary>
/// The <c>session.turns</c> source — a thread's prior top-level turns' EFFECTIVE attempts (<see cref="SessionTurnAttempts"/>)
/// rendered with their FULL, UN-CLIPPED results. This is the pull-not-push complement to <see cref="SessionContextBuilder"/>:
/// the launch digest clips each turn's result to <see cref="SessionTurnText.MaxResultChars"/> so the prompt stays
/// bounded; when the agent needs the whole thing back it pulls it here. Reads the SAME clean sources the digest does
/// (each run's launch goal + its declared <c>OutputsJson</c> result + produced branch — preferring the run's <see cref="PublishManifest"/> row over the raw
/// <c>OutputsJson.branch</c> guess, via the shared <see cref="SessionManifestBranches"/> choke point, and the shared
/// <see cref="SessionTurnText"/> for everything else) so the two never drift — the only difference is no per-turn clip.
/// Newest-first, team- + session-scoped, bounded by a total-character budget (a giant thread can't blow up the
/// model's context); an optional <see cref="AgentContextQuery.Query"/> filters to turns whose goal/result contains it
/// (case-insensitive).
///
/// <para>The effective-attempt resolution AND an optional query match both run INSIDE PostgreSQL, before any row
/// crosses the DB/CLR boundary — an old turn's exact match is found however far behind the newest
/// <see cref="MaxTurnsScanned"/> it sits, and only each kept row's named leaves (never an unrelated sibling JSON
/// field, never the whole <c>OutputsJson</c> root) ever reach this process.</para>
/// </summary>
public sealed class SessionTurnsContextSource : IContextSource, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;

    /// <summary>Cap the rows the DB query returns — a thread longer than this carries its older turns in the rolling summary, or is reached by an exact <see cref="AgentContextQuery.Query"/> match, not by unbounded scanning here (pinned by a test).</summary>
    internal const int MaxTurnsScanned = 50;

    /// <summary>Total-character budget for the rendered output, AND the per-leaf read clip applied inside the SQL query — newest turns are kept first; older ones beyond it are noted, not silently dropped (pinned by a test).</summary>
    internal const int MaxOutputChars = 40_000;

    public SessionTurnsContextSource(CodeSpaceDbContext db, IPublishManifestStore manifests)
    {
        _db = db;
        _manifests = manifests;
    }

    public string Kind => "session.turns";

    public string Description =>
        "The prior turns of this work thread with their FULL, un-clipped results (the launch digest clips them). " +
        "Optional 'query' filters to turns mentioning it. Returns nothing when the run is not part of a thread.";

    public async Task<AgentContextResult> RetrieveAsync(AgentContextQuery query, CancellationToken cancellationToken)
    {
        if (query.SessionId is not { } sessionId) return AgentContextResult.Empty;

        var hasQuery = !string.IsNullOrWhiteSpace(query.Query);

        var rows = await _db.Database.SqlQueryRaw<DbRow>(ListSql,
        [
            new NpgsqlParameter<Guid>("session_id", sessionId),
            new NpgsqlParameter<Guid>("team_id", query.TeamId),
            new NpgsqlParameter<bool>("query_provided", hasQuery),
            new NpgsqlParameter<string>("pattern", hasQuery ? $"%{EscapeLikePattern(query.Query!.Trim())}%" : ""),
            new NpgsqlParameter<int>("leaf_take", MaxOutputChars),
            new NpgsqlParameter<int>("row_limit", MaxTurnsScanned),
        ]).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return AgentContextResult.Empty;

        var turns = rows.Select(r => new TurnRow(r.Id, r.SessionTurnIndex, r.Status, r.Goal, r.Result, r.LegacyBranch)).ToList();

        var manifestsByRunId = await _manifests.ListForWorkflowRunsAsync(turns.Select(t => t.Id).ToList(), query.TeamId, cancellationToken).ConfigureAwait(false);

        var rendered = turns.Select(t => Render(t, manifestsByRunId.GetValueOrDefault(t.Id))).ToList();

        // The SQL match is a superset check (it matches any of goal/summary/combined/reason, not necessarily the ONE
        // leaf that wins the digest's summary/combined/reason precedence) — re-validated here against the actually
        // rendered text so a source that matched on a leaf that didn't end up rendered can't produce a false hit.
        var matched = Filter(rendered, query.Query);

        if (matched.Count == 0) return AgentContextResult.Empty;

        return AgentContextResult.From(Compose(matched));
    }

    /// <summary>
    /// Effective-attempt resolution (<see cref="SessionTurnAttempts"/>'s "newest Success, else newest overall" rule)
    /// runs in the <c>effective</c> CTE — a rerun's row (NULL turn index) groups with its original via
    /// <c>root_run_id</c>, and <c>DISTINCT ON</c> keeps only the winner per group, exactly like the C# resolver. The
    /// keyword match (when supplied) runs in the final WHERE against the RAW leaf values — before any <c>left()</c>
    /// clip — so an old exact match is never windowed away before it is even searched for.
    /// </summary>
    private const string ListSql = """
        /* session-turns-context:list */
        WITH lineage AS (
            SELECT r.id, COALESCE(r.root_run_id, r.id) AS group_id, r.session_turn_index, r.status, r.created_date
            FROM workflow_run AS r
            WHERE r.session_id = @session_id AND r.team_id = @team_id
        ),
        turn_groups AS (
            SELECT group_id, MAX(session_turn_index) FILTER (WHERE session_turn_index IS NOT NULL) AS turn
            FROM lineage
            GROUP BY group_id
            HAVING MAX(session_turn_index) FILTER (WHERE session_turn_index IS NOT NULL) IS NOT NULL
        ),
        effective AS (
            SELECT DISTINCT ON (l.group_id) l.group_id, l.id AS effective_id, g.turn
            FROM lineage AS l
            JOIN turn_groups AS g ON g.group_id = l.group_id
            ORDER BY l.group_id, (l.status = 'Success') DESC, l.created_date DESC, l.id DESC
        )
        SELECT
            e.effective_id AS id,
            e.turn AS session_turn_index,
            r.status AS status,
            left(CASE WHEN jsonb_typeof(q.normalized_payload_json -> 'goal') = 'string' AND btrim(q.normalized_payload_json ->> 'goal') <> '' THEN q.normalized_payload_json ->> 'goal' END, @leaf_take) AS goal,
            left(COALESCE(
                CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'summary') = 'string' AND btrim(r.outputs_jsonb ->> 'summary') <> '' THEN r.outputs_jsonb ->> 'summary' END,
                CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'combined') = 'string' AND btrim(r.outputs_jsonb ->> 'combined') <> '' THEN r.outputs_jsonb ->> 'combined' END,
                CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'reason') = 'string' AND btrim(r.outputs_jsonb ->> 'reason') <> '' THEN r.outputs_jsonb ->> 'reason' END
            ), @leaf_take) AS result,
            CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'branch') = 'string' AND btrim(r.outputs_jsonb ->> 'branch') <> '' THEN r.outputs_jsonb ->> 'branch' END AS legacy_branch
        FROM effective AS e
        JOIN workflow_run AS r ON r.id = e.effective_id
        LEFT JOIN workflow_run_request AS q ON q.id = r.run_request_id AND q.team_id = @team_id
        WHERE (@query_provided = FALSE OR (
            (q.normalized_payload_json ->> 'goal') ILIKE @pattern ESCAPE '\'
            OR (r.outputs_jsonb ->> 'summary') ILIKE @pattern ESCAPE '\'
            OR (r.outputs_jsonb ->> 'combined') ILIKE @pattern ESCAPE '\'
            OR (r.outputs_jsonb ->> 'reason') ILIKE @pattern ESCAPE '\'
        ))
        ORDER BY e.turn DESC
        LIMIT @row_limit
        """;

    /// <summary>Escape LIKE/ILIKE wildcards (<c>%</c>, <c>_</c>) and the escape character itself, so a query containing them is matched LITERALLY, not as a pattern.</summary>
    private static string EscapeLikePattern(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>Render one turn FULL (un-clipped) — same fields + order as the digest, just without <see cref="SessionTurnText.Clip"/>.</summary>
    private static RenderedTurn Render(TurnRow row, IReadOnlyList<PublishManifest>? manifests)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Turn {row.Turn} ({row.Status})");

        if (row.Goal != null) sb.AppendLine($"Asked: {row.Goal}");

        if (row.Result != null) sb.AppendLine($"Result: {row.Result}");

        var branch = SessionManifestBranches.ResolveSingleRepoBranch(manifests)?.Branch ?? row.LegacyBranch;
        if (branch != null) sb.AppendLine($"Produced branch: {branch}");

        return new RenderedTurn(row.Turn, sb.ToString().TrimEnd());
    }

    /// <summary>Keep only turns whose rendered text contains the (case-insensitive) query; a blank query keeps all.</summary>
    private static List<RenderedTurn> Filter(List<RenderedTurn> rendered, string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? rendered
            : rendered.Where(t => t.Text.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Appended when the single newest turn is itself larger than the whole budget and had to be clipped — so the bound holds even for one giant turn (never a silent truncation).</summary>
    internal const string TurnTruncationMarker = "\n…(this turn's result was truncated to fit the size budget — refine with a query, or read session.summary for the distilled older work.)";

    /// <summary>
    /// Compose the matched turns into one document: keep the most-recent turns that fit the character budget (the list
    /// arrives newest-first), then render them chronologically so it reads top-to-bottom. When the budget drops older
    /// matched turns, say so (never a silent truncation). The newest turn is ALWAYS kept (never an empty result) — but
    /// if it ALONE exceeds the budget (a single huge un-clipped result), it is clipped to fit with a marker, so one
    /// pull can never blow up the model's context (the <see cref="IContextSource"/> bound).
    /// </summary>
    private static string Compose(List<RenderedTurn> matchedNewestFirst)
    {
        var kept = new List<RenderedTurn>();
        var used = 0;

        foreach (var turn in matchedNewestFirst)
        {
            var cost = turn.Text.Length + 2;   // + the blank-line separator

            if (kept.Count > 0 && used + cost > MaxOutputChars) break;

            kept.Add(turn);
            used += cost;
        }

        // The newest turn is kept unconditionally above; if that single turn overshoots the budget on its own, clip it
        // (only the first turn can — any later turn was admitted only because the running total still fit).
        if (kept.Count == 1 && kept[0].Text.Length > MaxOutputChars)
            kept[0] = kept[0] with { Text = ClipToBudget(kept[0].Text) };

        var omitted = matchedNewestFirst.Count - kept.Count;

        var sb = new StringBuilder();
        sb.AppendLine("# Full prior turns in this work thread");

        if (omitted > 0)
            sb.AppendLine($"({omitted} older matching turn(s) omitted to fit the size budget — refine with a query, or read session.summary for the distilled older work.)");

        foreach (var turn in Enumerable.Reverse(kept))
        {
            sb.AppendLine();
            sb.AppendLine(turn.Text);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Clip one over-budget turn's text to the budget, leaving room for the truncation marker.</summary>
    private static string ClipToBudget(string text) => text[..(MaxOutputChars - TurnTruncationMarker.Length)] + TurnTruncationMarker;

    private readonly record struct TurnRow(Guid Id, int? Turn, string Status, string? Goal, string? Result, string? LegacyBranch);

    private readonly record struct RenderedTurn(int? Turn, string Text);

    private sealed class DbRow
    {
        public Guid Id { get; set; }
        public int? SessionTurnIndex { get; set; }
        public string Status { get; set; } = "";
        public string? Goal { get; set; }
        public string? Result { get; set; }
        public string? LegacyBranch { get; set; }
    }
}
