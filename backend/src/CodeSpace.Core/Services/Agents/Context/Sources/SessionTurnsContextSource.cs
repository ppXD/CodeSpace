using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

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
/// model's context); an optional <see cref="AgentContextQuery.Query"/> filters to turns whose goal, result, status, or
/// raw legacy branch mentions it (case-insensitive) — see <see cref="Description"/> for the one narrowing this leaves.
///
/// <para>The effective-attempt resolution AND the goal/result/status match both run INSIDE PostgreSQL, against each
/// leaf's FULL (never clipped) value, before any row crosses the DB/CLR boundary — an old turn's exact match is found
/// however far behind the newest <see cref="MaxTurnsScanned"/> it sits, OR however far past the per-leaf DISPLAY clip
/// it sits, and only each kept row's named leaves (never an unrelated sibling JSON field, never the whole
/// <c>OutputsJson</c> root) ever reach this process. The raw legacy branch column is ALSO matched in SQL as a superset
/// check ONLY (PostgreSQL cannot resolve a <see cref="PublishManifest"/> row) — a row admitted SOLELY via that column
/// is re-checked in C# against the SAME manifest-preferred branch that actually renders, so a query that only matched
/// a superseded raw guess can never produce a false hit.</para>
/// </summary>
public sealed class SessionTurnsContextSource : IContextSource, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;

    /// <summary>Cap each DB page. Older rows remain reachable through the source-owned keyset cursor; an exact <see cref="AgentContextQuery.Query"/> is applied before this bound (pinned by tests).</summary>
    internal const int MaxTurnsScanned = 50;

    /// <summary>Total-character budget for the rendered output, AND the per-leaf DISPLAY clip applied inside the SQL query's SELECT list — newest turns are kept first; older ones beyond it are noted, not silently dropped (pinned by a test). The query MATCH (the WHERE clause) runs against each leaf's full, unclipped value, so a match past this display clip is still found.</summary>
    internal const int MaxOutputChars = 40_000;

    public SessionTurnsContextSource(CodeSpaceDbContext db, IPublishManifestStore manifests)
    {
        _db = db;
        _manifests = manifests;
    }

    public string Kind => "session.turns";

    public string Description =>
        "The prior turns of this work thread with their FULL, un-clipped results (the launch digest clips them). " +
        "Optional 'query' filters to turns whose goal, result, status, or produced branch mentions it. A branch " +
        "resolved from this repository's publish record (rather than the run's own raw output) is not matched by " +
        "'query' — read without a query, or read session.summary, to find it another way. Returns nothing when the " +
        "run is not part of a thread.";

    public async Task<AgentContextResult> RetrieveAsync(AgentContextQuery query, CancellationToken cancellationToken)
    {
        if (query.SessionId is not { } sessionId) return AgentContextResult.Empty;

        var hasQuery = !string.IsNullOrWhiteSpace(query.Query);
        var cursor = SessionTurnsContextCursor.Decode(query.Cursor, query.TeamId, sessionId, query.Query);

        var rows = await _db.Database.SqlQueryRaw<DbRow>(ListSql,
        [
            new NpgsqlParameter<Guid>("session_id", sessionId),
            new NpgsqlParameter<Guid>("team_id", query.TeamId),
            new NpgsqlParameter<bool>("query_provided", hasQuery),
            new NpgsqlParameter<string>("pattern", hasQuery ? $"%{EscapeLikePattern(query.Query!.Trim())}%" : ""),
            new NpgsqlParameter<int>("leaf_take", MaxOutputChars),
            NullableParameter("before_turn", NpgsqlDbType.Integer, cursor?.Turn),
            NullableParameter("before_group_id", NpgsqlDbType.Uuid, cursor?.GroupId),
            new NpgsqlParameter<int>("row_limit", MaxTurnsScanned + 1),
        ]).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return AgentContextResult.Empty;

        var hasMoreRows = rows.Count > MaxTurnsScanned;
        var turns = rows.Take(MaxTurnsScanned).Select(r => new TurnRow(r.Id, r.GroupId, r.SessionTurnIndex, r.Status, r.Goal, r.Result, r.LegacyBranch, r.MatchedWithoutBranch)).ToList();

        var manifestsByRunId = await _manifests.ListForWorkflowRunsAsync(turns.Select(t => t.Id).ToList(), query.TeamId, cancellationToken).ConfigureAwait(false);

        // SQL already matched goal/result/status against their FULL (unclipped) values — trusted outright, never
        // re-validated against the (possibly clipped) rendered copy. The raw legacy branch column is a superset check
        // ONLY, so a row admitted SOLELY on it is re-checked here against the branch that actually renders.
        var matchedTurns = hasQuery
            ? turns.Where(t => t.MatchedWithoutBranch || BranchMatches(t, manifestsByRunId.GetValueOrDefault(t.Id), query.Query!)).ToList()
            : turns;

        if (matchedTurns.Count == 0)
            return hasMoreRows ? AgentContextResult.Partial("", CursorFor(turns[^1], query, sessionId)) : AgentContextResult.Empty;

        var rendered = matchedTurns.Select(t => Render(t, manifestsByRunId.GetValueOrDefault(t.Id))).ToList();
        var page = Compose(rendered);
        var bodyCut = page.KeptCount < matchedTurns.Count;
        var continuationRow = bodyCut ? matchedTurns[page.KeptCount - 1] : hasMoreRows ? turns[^1] : (TurnRow?)null;

        return continuationRow is { } last
            ? AgentContextResult.Partial(WithPartialCoverage(page.Text), CursorFor(last, query, sessionId))
            : AgentContextResult.From(page.Text);
    }

    /// <summary>
    /// Effective-attempt resolution (<see cref="SessionTurnAttempts"/>'s "newest Success, else newest overall" rule)
    /// runs in the <c>effective</c> CTE — a rerun's row (NULL turn index) groups with its original via
    /// <c>root_run_id</c>, and <c>DISTINCT ON</c> keeps only the winner per group, exactly like the C# resolver. The
    /// <c>resolved</c> CTE computes each candidate leaf ONCE at FULL length; the final SELECT clips goal/result to
    /// <c>@leaf_take</c> for DISPLAY only — the keyword match (when supplied) runs against the FULL values (goal,
    /// result, status) plus the raw legacy branch, wrapped in <c>COALESCE(…, '')</c> so an absent leaf compares as
    /// "no match" rather than SQL's NULL propagating into <c>matched_without_branch</c> (a NOT NULL boolean C# reads).
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
        ),
        resolved AS (
            SELECT
                e.effective_id AS id,
                e.group_id AS group_id,
                e.turn AS session_turn_index,
                r.status AS status,
                CASE WHEN jsonb_typeof(q.normalized_payload_json -> 'goal') = 'string' AND btrim(q.normalized_payload_json ->> 'goal') <> '' THEN q.normalized_payload_json ->> 'goal' END AS goal_full,
                COALESCE(
                    CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'summary') = 'string' AND btrim(r.outputs_jsonb ->> 'summary') <> '' THEN r.outputs_jsonb ->> 'summary' END,
                    CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'combined') = 'string' AND btrim(r.outputs_jsonb ->> 'combined') <> '' THEN r.outputs_jsonb ->> 'combined' END,
                    CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'reason') = 'string' AND btrim(r.outputs_jsonb ->> 'reason') <> '' THEN r.outputs_jsonb ->> 'reason' END
                ) AS result_full,
                CASE WHEN jsonb_typeof(r.outputs_jsonb -> 'branch') = 'string' AND btrim(r.outputs_jsonb ->> 'branch') <> '' THEN r.outputs_jsonb ->> 'branch' END AS legacy_branch
            FROM effective AS e
            JOIN workflow_run AS r ON r.id = e.effective_id
            LEFT JOIN workflow_run_request AS q ON q.id = r.run_request_id AND q.team_id = @team_id
        )
        SELECT
            id,
            group_id,
            session_turn_index,
            status,
            left(goal_full, @leaf_take) AS goal,
            left(result_full, @leaf_take) AS result,
            legacy_branch,
            (@query_provided = FALSE
                OR COALESCE(goal_full, '') ILIKE @pattern ESCAPE '\'
                OR COALESCE(result_full, '') ILIKE @pattern ESCAPE '\'
                OR status ILIKE @pattern ESCAPE '\'
            ) AS matched_without_branch
        FROM resolved
        WHERE (@query_provided = FALSE
            OR COALESCE(goal_full, '') ILIKE @pattern ESCAPE '\'
            OR COALESCE(result_full, '') ILIKE @pattern ESCAPE '\'
            OR status ILIKE @pattern ESCAPE '\'
            OR COALESCE(legacy_branch, '') ILIKE @pattern ESCAPE '\'
        )
          AND (@before_turn IS NULL OR (session_turn_index, group_id) < (@before_turn, @before_group_id))
        ORDER BY session_turn_index DESC, group_id DESC
        LIMIT @row_limit
        """;

    private static NpgsqlParameter NullableParameter(string name, NpgsqlDbType type, object? value) => new(name, type) { Value = value ?? DBNull.Value };

    private static string CursorFor(TurnRow row, AgentContextQuery query, Guid sessionId) =>
        new SessionTurnsContextCursor(row.Turn, row.GroupId).Encode(query.TeamId, sessionId, query.Query);

    /// <summary>Escape LIKE/ILIKE wildcards (<c>%</c>, <c>_</c>) and the escape character itself, so a query containing them is matched LITERALLY, not as a pattern.</summary>
    private static string EscapeLikePattern(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>True when the RENDERED branch — the SAME manifest-preferred (I2) branch <see cref="Render"/> shows, falling back to the raw legacy one — contains the query. Only consulted for a row SQL admitted SOLELY via the raw legacy branch column, to guard against a manifest that has since superseded it.</summary>
    private static bool BranchMatches(TurnRow row, IReadOnlyList<PublishManifest>? manifests, string query)
    {
        var branch = SessionManifestBranches.ResolveSingleRepoBranch(manifests)?.Branch ?? row.LegacyBranch;
        return branch != null && branch.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>Appended when the single newest turn is itself larger than the whole budget and had to be clipped — so the bound holds even for one giant turn (never a silent truncation).</summary>
    internal const string TurnTruncationMarker = "\n…(this turn's result was truncated to fit the size budget — refine with a query, or read session.summary for the distilled older work.)";

    /// <summary>
    /// Compose the matched turns into one document: keep the most-recent turns that fit the character budget (the list
    /// arrives newest-first), then render them chronologically so it reads top-to-bottom. When the budget drops older
    /// matched turns, say so (never a silent truncation). The newest turn is ALWAYS kept (never an empty result) — but
    /// if it ALONE exceeds the budget (a single huge un-clipped result), it is clipped to fit with a marker, so one
    /// pull can never blow up the model's context (the <see cref="IContextSource"/> bound).
    /// </summary>
    private static ComposedPage Compose(List<RenderedTurn> matchedNewestFirst)
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

        var sb = new StringBuilder();
        sb.AppendLine("# Full prior turns in this work thread");

        foreach (var turn in Enumerable.Reverse(kept))
        {
            sb.AppendLine();
            sb.AppendLine(turn.Text);
        }

        return new ComposedPage(sb.ToString().TrimEnd(), kept.Count);
    }

    private static string WithPartialCoverage(string text) => text.Replace("# Full prior turns in this work thread", "# Full prior turns in this work thread\n(Older matching turns may be omitted from this partial page. Continue with the returned source cursor; do not infer that older evidence is absent.)", StringComparison.Ordinal);

    /// <summary>Clip one over-budget turn's text to the budget, leaving room for the truncation marker.</summary>
    private static string ClipToBudget(string text) => text[..(MaxOutputChars - TurnTruncationMarker.Length)] + TurnTruncationMarker;

    private readonly record struct TurnRow(Guid Id, Guid GroupId, int Turn, string Status, string? Goal, string? Result, string? LegacyBranch, bool MatchedWithoutBranch);

    private readonly record struct RenderedTurn(int Turn, string Text);

    private readonly record struct ComposedPage(string Text, int KeptCount);

    private sealed class DbRow
    {
        public Guid Id { get; set; }
        public Guid GroupId { get; set; }
        public int SessionTurnIndex { get; set; }
        public string Status { get; set; } = "";
        public string? Goal { get; set; }
        public string? Result { get; set; }
        public string? LegacyBranch { get; set; }
        public bool MatchedWithoutBranch { get; set; }
    }
}
