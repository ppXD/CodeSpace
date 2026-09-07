using System.Text.Json;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="ISessionContextBuilder"/>. Reads the session's prior top-level turns' EFFECTIVE attempts
/// (<see cref="SessionTurnAttempts"/> — a rerun that fixed a failed original wins) from CLEAN sources only — each
/// turn's launch goal and its declared result, read as narrow lowercase JSON leaves by
/// <see cref="ISessionIntelligenceTurnReader"/> without transporting either persisted JSON root, plus a produced branch —
/// preferring the run's <see cref="PublishManifest"/> row (I2's single source of truth) over the raw
/// legacy <c>OutputsJson.branch</c> compatibility leaf, via the same <see cref="SessionManifestBranches"/> choke point
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
    private readonly ISessionIntelligenceTurnReader _turns;

    /// <summary>Cap the VERBATIM window to the most recent N top-level turns — recent work is rendered in full; older turns roll into the distilled <see cref="WorkSession.Summary"/>. The summarizer's watermark uses this same window size, so summary + window are contiguous.</summary>
    internal const int MaxTurns = 8;

    public SessionContextBuilder(CodeSpaceDbContext db, IPublishManifestStore manifests)
    {
        _db = db;
        _manifests = manifests;
        _turns = new SessionIntelligenceTurnReader(db);
    }

    public async Task<string?> BuildAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        // The session's WHOLE narrow lineage (a rerun/replay attempt inherits the SessionId with a NULL turn index) —
        // each turn's EFFECTIVE attempt (SessionTurnAttempts) is resolved before windowing, so a superseded original
        // never shadows its own successful rerun. The shared reader extracts only prompt leaves, never the JSON roots.
        var lineage = await _turns.ListAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);

        var window = lineage.GroupBy(r => r.RootRunId ?? r.Id)
            .Where(g => g.Any(r => r.SessionTurnIndex != null))
            .Select(g => new { Turn = g.First(r => r.SessionTurnIndex != null).SessionTurnIndex, EffectiveId = SessionTurnAttempts.ResolveEffectiveId(g.Select(r => new SessionTurnAttempts.AttemptRow(r.Id, r.Status, r.CreatedDate))) })
            .Join(lineage, t => t.EffectiveId, r => r.Id, (t, r) => new { t.Turn, r.Id, r.Status, r.Goal, r.Result, r.LegacyBranch })
            .OrderByDescending(r => r.Turn)
            .Take(MaxTurns)
            .ToList();

        if (window.Count == 0) return null;

        var manifestsByRunId = await _manifests.ListForWorkflowRunsAsync(window.Select(r => r.Id).ToList(), teamId, cancellationToken).ConfigureAwait(false);

        // P4-U3 (L5 contract carry): each turn's LATEST durable completion assessment rides the digest, so the
        // continuing planner sees the CONTRACT state — a failed oracle, a parked delivery, an unsettled obligation
        // — not just the turn's own prose. One batched read; turns with no record (legacy / non-terminal) render
        // exactly as before.
        var windowIds = window.Select(r => r.Id).ToList();
        var assessmentsByRunId = (await _db.CompletionAssessmentRecord.AsNoTracking()
            .Where(a => a.TeamId == teamId && windowIds.Contains(a.WorkflowRunId))
            .OrderBy(a => a.CreatedDate)
            .Select(a => new { a.WorkflowRunId, a.AssessmentJson, a.WouldBeTerminalDecision })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(a => a.WorkflowRunId)
            .ToDictionary(g => g.Key, g => g.Last());

        // The distilled summary of OLDER turns (those scrolled out of the window), if the thread has grown past it.
        // Null for a short thread / when no model was available to distill ⇒ the digest is just the recent window.
        // TRACKING (not AsNoTracking/projection) so it identity-resolves to the summary the summarizer just STAGED in
        // this same unit of work (the write commits with the run) — a DB read would miss the un-flushed change.
        var session = await _db.WorkSession
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        var summary = session?.Summary;

        // Older, FOLDED turns can still carry an unresolved contract even once their prose has scrolled out of the
        // window above — read from the durable source binding (never re-derived from the summary's own prose), so a
        // persisted failed verification or unknown delivery is never silently lost to compaction.
        var carriedForward = await BuildCarriedForwardContractsAsync(session?.SummarySourceBindingJson, teamId, cancellationToken).ConfigureAwait(false);

        var sb = new StringBuilder();
        sb.AppendLine("# Earlier turns in this work thread");
        sb.AppendLine("You are continuing an existing thread. Build on the work below — do not redo it.");

        if (!string.IsNullOrWhiteSpace(summary) || session?.SummaryStaleSinceTurn is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Summary of earlier work (older turns, distilled)");

            if (!string.IsNullOrWhiteSpace(summary)) sb.AppendLine(summary!.Trim());

            // Fail-open distillation (SessionSummarizer) leaves Summary UNCHANGED — or, on a FIRST-EVER failure,
            // leaves it null — on a model/LLM error. Never silent here (even with no prior prose to prepend), since
            // an unflagged gap could mislead the continuing agent about what already happened before the gap turn.
            if (session?.SummaryStaleSinceTurn is { } staleSince)
                sb.AppendLine($"(Note: turns from {staleSince} onward have not yet been folded into this summary — it may be incomplete.)");
        }

        if (carriedForward.Count > 0)
        {
            // The qualifier names the fold's OWN watermark — these verdicts are current AS OF that fold, not
            // necessarily right now (SessionSummarizer is fail-open, so a later drift may not have been refreshed
            // yet; a per-line flag below covers the specific case where a newer assessment already exists).
            var throughTurn = session?.SummaryThroughTurnIndex?.ToString() ?? "unknown";
            sb.AppendLine();
            sb.AppendLine($"## Unresolved contracts carried forward from earlier (summarized) turns (as of the last fold — turn {throughTurn})");
            foreach (var line in carriedForward) sb.AppendLine(line);
        }

        foreach (var row in Enumerable.Reverse(window))
        {
            sb.AppendLine();
            sb.AppendLine($"## Turn {row.Turn} ({row.Status})");

            if (row.Goal != null) sb.AppendLine($"Asked: {SessionTurnText.Clip(row.Goal)}");

            if (row.Result != null) sb.AppendLine($"Result: {SessionTurnText.Clip(row.Result)}");

            var branch = SessionManifestBranches.ResolveSingleRepoBranch(manifestsByRunId.GetValueOrDefault(row.Id))?.Branch ?? row.LegacyBranch;
            if (branch != null) sb.AppendLine($"Produced branch: {branch}");

            if (assessmentsByRunId.TryGetValue(row.Id, out var recorded) && RenderCompletion(recorded.AssessmentJson, recorded.WouldBeTerminalDecision) is { } completion)
                sb.AppendLine(completion);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Every durably-bound older turn whose LAST-RECORDED assessment was unresolved, rendered as one line each. For
    /// a turn bound to a specific assessment, reads it by that EXACT id (the one the fold actually saw), never
    /// "whatever is latest now" for that run (a source change behind the watermark is <c>SessionSummarizer</c>'s job
    /// to detect and refresh, not this digest's) — except that refresh is fail-open (a drift may not have been
    /// picked up yet), so a line whose run now has a NEWER recorded assessment than the bound one says so; the bound
    /// verdict still renders, flagged as possibly superseded rather than presented as current. A turn folded while
    /// its run had NO assessment yet (bound with a null id) is never a permanent blind spot: if the run has SINCE
    /// been assessed, that current latest assessment renders instead, flagged as recorded after the fold —
    /// <see cref="SessionSummarizer"/>'s own dirty-check (a null-to-non-null <c>AssessmentId</c>) self-heals the
    /// binding too, but only starting the next launch that runs the summarizer; this covers the launch(es) in
    /// between. A turn with no assessment either at fold time or now still renders nothing. Bounded to the
    /// (typically tiny) set of bound turns that ever had, or now have, an assessment.
    /// </summary>
    private async Task<IReadOnlyList<string>> BuildCarriedForwardContractsAsync(string? bindingJson, Guid teamId, CancellationToken cancellationToken)
    {
        var bindings = SessionSummarySourceBindings.Parse(bindingJson).OrderBy(b => b.Turn).ToList();

        if (bindings.Count == 0) return [];

        var latestAssessmentIdByRunId = await LoadLatestAssessmentIdsAsync(teamId, bindings.Select(b => b.EffectiveRunId).Distinct().ToList(), cancellationToken).ConfigureAwait(false);

        var assessmentIds = bindings.Where(b => b.AssessmentId is not null).Select(b => b.AssessmentId!.Value)
            .Concat(latestAssessmentIdByRunId.Values)
            .Distinct().ToList();

        var assessmentsById = (await _db.CompletionAssessmentRecord.AsNoTracking()
            .Where(a => a.TeamId == teamId && assessmentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AssessmentJson, a.WouldBeTerminalDecision })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(a => a.Id, a => a);

        var lines = new List<string>();

        foreach (var binding in bindings)
        {
            var hasLatest = latestAssessmentIdByRunId.TryGetValue(binding.EffectiveRunId, out var latestId);

            if (binding.AssessmentId is null && !hasLatest) continue;   // never assessed at fold time, and still nothing recorded now

            var renderedId = binding.AssessmentId ?? latestId;

            if (!assessmentsById.TryGetValue(renderedId, out var recorded)) continue;

            if (RenderCompletion(recorded.AssessmentJson, recorded.WouldBeTerminalDecision) is not { } completion) continue;

            string flag;
            if (binding.AssessmentId is null) flag = " [an assessment was recorded for this run AFTER the last fold]";
            else if (hasLatest && latestId != binding.AssessmentId) flag = " [a NEWER assessment now exists for this run — this verdict may be superseded]";
            else flag = "";

            lines.Add($"Turn {binding.Turn} (run {binding.EffectiveRunId}, assessment {renderedId}): {completion}{flag}");
        }

        return lines;
    }

    /// <summary>The latest <c>CompletionAssessmentRecord.Id</c> per <c>WorkflowRunId</c>, for the given (already-narrow) run ids — an id-only read, never the assessment body. Mirrors <c>SessionSummarizer</c>'s own lookup (the SAME (CreatedDate, Id) tie-break), so "latest" means the same thing in both places.</summary>
    private async Task<IReadOnlyDictionary<Guid, Guid>> LoadLatestAssessmentIdsAsync(Guid teamId, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return new Dictionary<Guid, Guid>();

        return (await _db.CompletionAssessmentRecord.AsNoTracking()
            .Where(a => a.TeamId == teamId && runIds.Contains(a.WorkflowRunId))
            .OrderBy(a => a.CreatedDate).ThenBy(a => a.Id)
            .Select(a => new { a.WorkflowRunId, a.Id })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(a => a.WorkflowRunId)
            .ToDictionary(g => g.Key, g => g.Last().Id);
    }

    /// <summary>The turn's contract verdict in ONE legible line — dimensions that are fine are omitted, so a clean turn reads clean and an unclean one names exactly what is still owed. Null (no line) when everything is settled positive; a malformed record renders nothing rather than a wrong claim.</summary>
    internal static string? RenderCompletion(string assessmentJson, string? wouldBeTerminalDecision)
    {
        Messages.Contracts.CompletionAssessment? assessment;

        try { assessment = JsonSerializer.Deserialize<Messages.Contracts.CompletionAssessment>(assessmentJson, Agents.AgentJson.Options); }
        catch (JsonException) { return null; }

        if (assessment is null) return null;

        var concerns = new List<string>();

        if (assessment.Outcome != Messages.Contracts.OutcomeDisposition.Solved) concerns.Add($"outcome={assessment.Outcome}");
        if (assessment.Verification is not (Messages.Contracts.VerificationDisposition.Passed or Messages.Contracts.VerificationDisposition.NotApplicable)) concerns.Add($"verification={assessment.Verification}");
        if (assessment.Artifact is not (Messages.Contracts.ArtifactDisposition.Captured or Messages.Contracts.ArtifactDisposition.NothingExpected)) concerns.Add($"artifact={assessment.Artifact}");
        if (assessment.Delivery is not (Messages.Contracts.DeliveryDisposition.Delivered or Messages.Contracts.DeliveryDisposition.NotRequired)) concerns.Add($"delivery={assessment.Delivery}");

        if (concerns.Count == 0) return null;

        var wouldBe = string.IsNullOrEmpty(wouldBeTerminalDecision) ? "" : $" — would-be terminal: {wouldBeTerminalDecision}";

        return $"UNRESOLVED CONTRACT: {string.Join(", ", concerns)}{wouldBe}. Address this before or alongside the new ask — it is still owed.";
    }
}
