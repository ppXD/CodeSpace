using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="ISessionContextBuilder"/>. Reads the session's prior top-level turns' EFFECTIVE attempts
/// (<see cref="SessionTurnAttempts"/> — a rerun that fixed a failed original wins) from CLEAN sources only — each
/// turn's launch goal (the request payload's <c>goal</c>) and its declared result (<c>OutputsJson</c>, read
/// generically across projection shapes via <see cref="SessionTurnText.ReadResult"/>, plus a produced branch —
/// preferring the run's <see cref="PublishManifest"/> row (I2's single source of truth) over the raw
/// <c>OutputsJson.branch</c> guess, via the same <see cref="SessionManifestBranches"/> choke point
/// <see cref="SessionProjection"/>/<see cref="SessionBranchResolver"/> use, so the branch injected into the NEXT
/// turn's own prompt never disagrees with what the room displays or what a CONTINUE clones from) — so the digest
/// never contains a previously-injected grounding block (no recursion). Renders the most recent
/// <see cref="MaxTurns"/> turns VERBATIM (each result clipped); turns OLDER than that window are carried by the
/// thread's rolling <see cref="WorkSession.Summary"/> (an LLM distillation maintained by <c>SessionSummarizer</c>),
/// prepended as a distilled prefix. So the injected context stays bounded however long the thread grows, without
/// silently dropping older work. The partial <c>idx_workflow_run_session</c> index (migration 0070) keeps the lookup
/// cheap. A thread that never exceeds the window has no summary ⇒ byte-identical to the pre-summary digest.
/// </summary>
public sealed class SessionContextBuilder : ISessionContextBuilder, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;

    /// <summary>Cap the VERBATIM window to the most recent N top-level turns — recent work is rendered in full; older turns roll into the distilled <see cref="WorkSession.Summary"/>. The summarizer's watermark uses this same window size, so summary + window are contiguous.</summary>
    internal const int MaxTurns = 8;

    public SessionContextBuilder(CodeSpaceDbContext db, IPublishManifestStore manifests)
    {
        _db = db;
        _manifests = manifests;
    }

    public async Task<string?> BuildAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        // The session's WHOLE lineage (a rerun/replay attempt inherits the SessionId with a NULL turn index) — each
        // turn's EFFECTIVE attempt (SessionTurnAttempts) is resolved before windowing, so a superseded original never
        // shadows its own successful rerun in the digest. One query — EF joins the request for the goal payload.
        var lineage = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.TeamId == teamId)
            .Select(r => new { r.Id, r.RootRunId, r.SessionTurnIndex, r.Status, r.CreatedDate, r.OutputsJson, Payload = r.RunRequest.NormalizedPayloadJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var window = lineage.GroupBy(r => r.RootRunId ?? r.Id)
            .Where(g => g.Any(r => r.SessionTurnIndex != null))
            .Select(g => new { Turn = g.First(r => r.SessionTurnIndex != null).SessionTurnIndex, EffectiveId = SessionTurnAttempts.ResolveEffectiveId(g.Select(r => new SessionTurnAttempts.AttemptRow(r.Id, r.Status, r.CreatedDate))) })
            .Join(lineage, t => t.EffectiveId, r => r.Id, (t, r) => new { t.Turn, r.Id, r.Status, r.OutputsJson, r.Payload })
            .OrderByDescending(r => r.Turn)
            .Take(MaxTurns)
            .ToList();

        if (window.Count == 0) return null;

        var manifestsByRunId = await _manifests.ListForWorkflowRunsAsync(window.Select(r => r.Id).ToList(), teamId, cancellationToken).ConfigureAwait(false);

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

            var branch = SessionManifestBranches.ResolveSingleRepoBranch(manifestsByRunId.GetValueOrDefault(row.Id)) ?? SessionTurnText.ReadString(row.OutputsJson, "branch");
            if (branch != null) sb.AppendLine($"Produced branch: {branch}");
        }

        return sb.ToString().TrimEnd();
    }
}
