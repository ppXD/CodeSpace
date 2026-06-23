using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="ISessionContextBuilder"/>. Reads the session's prior top-level turns from CLEAN sources only —
/// each run's launch goal (the request payload's <c>goal</c>) and its declared result (<c>OutputsJson</c>, read
/// generically across projection shapes — single-agent <c>summary</c>, plan-map <c>combined</c>, supervisor
/// <c>reason</c>, plus a produced <c>branch</c>) — so the digest never contains a previously-injected grounding
/// block (no recursion). Bounded to the most recent <see cref="MaxTurns"/> turns with each result clipped,
/// so the injected context stays small however long the thread grows; the partial <c>idx_workflow_run_session</c>
/// index (migration 0070) keeps the lookup cheap.
/// </summary>
public sealed class SessionContextBuilder : ISessionContextBuilder, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    /// <summary>Cap the digest to the most recent N top-level turns — recent work is what a follow-up builds on; older turns roll off (a distilled long-thread summary is a later refinement).</summary>
    internal const int MaxTurns = 8;

    /// <summary>Clip each turn's rendered result so one verbose turn can't blow up the prompt.</summary>
    internal const int MaxResultChars = 600;

    public SessionContextBuilder(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<string?> BuildAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        // The session timeline, most recent first (bounded), then flipped to chronological for rendering. One query —
        // EF joins the request for the goal payload. Only top-level turns (a child / replay has a null turn index).
        var window = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.TeamId == teamId && r.SessionTurnIndex != null)
            .OrderByDescending(r => r.SessionTurnIndex)
            .Take(MaxTurns)
            .Select(r => new { Turn = r.SessionTurnIndex, r.Status, r.OutputsJson, Payload = r.RunRequest.NormalizedPayloadJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (window.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("# Earlier turns in this work thread");
        sb.AppendLine("You are continuing an existing thread. Build on the work below — do not redo it.");

        foreach (var row in Enumerable.Reverse(window))
        {
            sb.AppendLine();
            sb.AppendLine($"## Turn {row.Turn} ({row.Status})");

            var goal = ReadString(row.Payload, "goal");
            if (goal != null) sb.AppendLine($"Asked: {Clip(goal)}");

            var result = ReadResult(row.OutputsJson);
            if (result != null) sb.AppendLine($"Result: {Clip(result)}");

            var branch = ReadString(row.OutputsJson, "branch");
            if (branch != null) sb.AppendLine($"Produced branch: {branch}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The prior turn's result text, read GENERICALLY across projection shapes from its declared outputs: a
    /// single-agent terminal surfaces <c>summary</c>, plan-map <c>combined</c>, supervisor <c>reason</c> — the first
    /// present wins, so a continue after ANY projection kind carries that turn's outcome forward (not just the goal).
    /// A new projection surfacing its result under another key adds it to this fallback chain.
    /// </summary>
    private static string? ReadResult(string outputsJson) =>
        ReadString(outputsJson, "summary") ?? ReadString(outputsJson, "combined") ?? ReadString(outputsJson, "reason");

    /// <summary>Read a non-blank string field from a JSON object, tolerating malformed / non-object / absent payloads (returns null).</summary>
    private static string? ReadString(string json, string field)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Clip(string s) => s.Length <= MaxResultChars ? s : s[..MaxResultChars] + "…";
}
