using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="ISessionContextBuilder"/>. Reads the session's prior top-level turns from CLEAN sources only —
/// each run's launch goal (the request payload's <c>goal</c>) and its declared result (<c>OutputsJson</c>, read
/// generically across projection shapes via <see cref="SessionTurnText.ReadResult"/>, plus a produced <c>branch</c>)
/// — so the digest never contains a previously-injected grounding block (no recursion). Renders the most recent
/// <see cref="MaxTurns"/> turns VERBATIM (each result clipped); turns OLDER than that window are carried by the
/// thread's rolling <see cref="WorkSession.Summary"/> (an LLM distillation maintained by <c>SessionSummarizer</c>),
/// prepended as a distilled prefix. So the injected context stays bounded however long the thread grows, without
/// silently dropping older work. The partial <c>idx_workflow_run_session</c> index (migration 0070) keeps the lookup
/// cheap. A thread that never exceeds the window has no summary ⇒ byte-identical to the pre-summary digest.
/// </summary>
public sealed class SessionContextBuilder : ISessionContextBuilder, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    /// <summary>Cap the VERBATIM window to the most recent N top-level turns — recent work is rendered in full; older turns roll into the distilled <see cref="WorkSession.Summary"/>. The summarizer's watermark uses this same window size, so summary + window are contiguous.</summary>
    internal const int MaxTurns = 8;

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

        // The distilled summary of OLDER turns (those scrolled out of the window), if the thread has grown past it.
        // Null for a short thread / when no model was available to distill ⇒ the digest is just the recent window.
        // TRACKING (not AsNoTracking/projection) so it identity-resolves to the summary the summarizer just STAGED in
        // this same unit of work (the write commits with the run) — a DB read would miss the un-flushed change.
        var session = await _db.WorkSession
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        var summary = session?.Summary;

        var sb = new StringBuilder();
        sb.AppendLine("# Earlier turns in this work thread");
        sb.AppendLine("You are continuing an existing thread. Build on the work below — do not redo it.");

        if (!string.IsNullOrWhiteSpace(summary))
        {
            sb.AppendLine();
            sb.AppendLine("## Summary of earlier work (older turns, distilled)");
            sb.AppendLine(summary.Trim());
        }

        foreach (var row in Enumerable.Reverse(window))
        {
            sb.AppendLine();
            sb.AppendLine($"## Turn {row.Turn} ({row.Status})");

            var goal = SessionTurnText.ReadString(row.Payload, "goal");
            if (goal != null) sb.AppendLine($"Asked: {SessionTurnText.Clip(goal)}");

            var result = SessionTurnText.ReadResult(row.OutputsJson);
            if (result != null) sb.AppendLine($"Result: {SessionTurnText.Clip(result)}");

            var branch = SessionTurnText.ReadString(row.OutputsJson, "branch");
            if (branch != null) sb.AppendLine($"Produced branch: {branch}");
        }

        return sb.ToString().TrimEnd();
    }
}
