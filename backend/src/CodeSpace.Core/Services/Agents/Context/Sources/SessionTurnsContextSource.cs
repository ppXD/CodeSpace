using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Context.Sources;

/// <summary>
/// The <c>session.turns</c> source — a thread's prior top-level turns rendered with their FULL, UN-CLIPPED results.
/// This is the pull-not-push complement to <see cref="SessionContextBuilder"/>: the launch digest clips each turn's
/// result to <see cref="SessionTurnText.MaxResultChars"/> so the prompt stays bounded; when the agent needs the whole
/// thing back it pulls it here. Reads the SAME clean sources the digest does (each run's launch goal + its declared
/// <c>OutputsJson</c> result + produced branch — preferring the run's <see cref="PublishManifest"/> row over the raw
/// <c>OutputsJson.branch</c> guess, via the shared <see cref="SessionManifestBranches"/> choke point, and the shared
/// <see cref="SessionTurnText"/> for everything else) so the two never drift — the only difference is no per-turn clip.
/// Newest-first, team- + session-scoped, bounded by a total-character budget (a giant thread can't blow up the
/// model's context); an optional <see cref="AgentContextQuery.Query"/> filters to turns whose goal/result contains it
/// (case-insensitive).
/// </summary>
public sealed class SessionTurnsContextSource : IContextSource, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;

    /// <summary>Cap the newest turns scanned from the DB — a thread longer than this carries its older turns in the rolling summary, not here (bounds the read; pinned by a test).</summary>
    internal const int MaxTurnsScanned = 50;

    /// <summary>Total-character budget for the rendered output — newest turns are kept first; older ones beyond it are noted, not silently dropped (pinned by a test).</summary>
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

        // The session timeline, newest first (bounded scan), each turn's clean sources only. Only top-level turns carry a
        // turn index (a child / replay / rerun inherits the SessionId but gets a NULL index — never a thread "turn").
        var turns = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.TeamId == query.TeamId && r.SessionTurnIndex != null)
            .OrderByDescending(r => r.SessionTurnIndex)
            .Take(MaxTurnsScanned)
            .Select(r => new TurnRow(r.Id, r.SessionTurnIndex, r.Status.ToString(), r.OutputsJson, r.RunRequest.NormalizedPayloadJson))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (turns.Count == 0) return AgentContextResult.Empty;

        var manifestsByRunId = await _manifests.ListForWorkflowRunsAsync(turns.Select(t => t.Id).ToList(), query.TeamId, cancellationToken).ConfigureAwait(false);

        var rendered = turns.Select(t => Render(t, manifestsByRunId.GetValueOrDefault(t.Id))).ToList();

        var matched = Filter(rendered, query.Query);

        if (matched.Count == 0) return AgentContextResult.Empty;

        return AgentContextResult.From(Compose(matched));
    }

    /// <summary>Render one turn FULL (un-clipped) — same fields + order as the digest, just without <see cref="SessionTurnText.Clip"/>.</summary>
    private static RenderedTurn Render(TurnRow row, IReadOnlyList<PublishManifest>? manifests)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## Turn {row.Turn} ({row.Status})");

        var goal = SessionTurnText.ReadString(row.Payload, "goal");
        if (goal != null) sb.AppendLine($"Asked: {goal}");

        var result = SessionTurnText.ReadResult(row.OutputsJson);
        if (result != null) sb.AppendLine($"Result: {result}");

        var branch = SessionManifestBranches.ResolveSingleRepoBranch(manifests) ?? SessionTurnText.ReadString(row.OutputsJson, "branch");
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

    private readonly record struct TurnRow(Guid Id, int? Turn, string Status, string OutputsJson, string Payload);

    private readonly record struct RenderedTurn(int? Turn, string Text);
}
