using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Workflows.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="ISessionSummarizer"/>. Folds the turns that have scrolled out of the digest's recent window
/// (those above the summary watermark) — each turn's EFFECTIVE attempt per <see cref="SessionTurnAttempts"/> — into
/// <c>WorkSession.Summary</c> via a plain-text LLM distillation, resolving the model from the team's pool the SAME way
/// <c>LlmWorkflowPlanner</c> does. Incremental (only the newly scrolled-out turns are folded into the existing summary)
/// and FAIL-OPEN (no pool model / LLM error ⇒ the summary is left unchanged and the launch proceeds with the recent
/// window only).
///
/// <para>Every already-folded turn also carries a durable <see cref="SessionSummarySourceBinding"/> (its effective run
/// id, a content fingerprint, its latest assessment id) — written for EVERY older turn on each successful fold, not
/// only the ones just folded, so a turn folded before this binding existed (a legacy row) is backfilled the next
/// time any fold succeeds, rather than staying unbound forever. On EVERY run, every BOUND turn is re-checked against
/// its CURRENT effective source — an unmoved watermark does not mean "nothing to do": if a bound turn's rerun has
/// since won, its result mutated, or a new assessment was recorded, the older-turns span is rebuilt fresh rather
/// than left silently stale behind an equal watermark. That full-span rebuild is retried at most once per known
/// failure though — while a PRIOR attempt is still unresolved (<c>WorkSession.SummaryStaleSinceTurn</c> set), a
/// launch falls back to the cheap incremental fold instead of re-paying for the whole span on every launch under a
/// sustained provider outage; it re-arms the next time any fold succeeds. That re-arm needs a LATER launch to ever
/// invoke this method again though — a session that goes dormant right after the failure keeps the disclosed
/// (never silent) gap forever, not just until the next launch. <see cref="SessionContextBuilder"/> reads
/// the binding to carry an out-of-window turn's unresolved contract forward without re-deriving it from this class's
/// own model-written prose.</para>
/// </summary>
public sealed class SessionSummarizer : ISessionSummarizer, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;
    private readonly ISessionIntelligenceTurnReader _turns;
    private readonly ILLMClientRegistry _clientRegistry;
    private readonly IModelPoolSelector _modelSelector;
    private readonly ILogger<SessionSummarizer> _logger;

    public SessionSummarizer(CodeSpaceDbContext db, IPublishManifestStore manifests, ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector, ILogger<SessionSummarizer> logger)
    {
        _db = db;
        _manifests = manifests;
        _turns = new SessionIntelligenceTurnReader(db);
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
        _logger = logger;
    }

    public async Task EnsureSummaryUpToDateAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        var olderTurns = await LoadOlderTurnsAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);

        if (olderTurns.Count == 0) return;

        var targetWatermark = olderTurns[0].Turn!.Value;   // newest of the older turns (first in desc order)

        // TRACKED load — the Summary write stages on the shared request-scoped DbContext, committing atomically with the run.
        var session = await _db.WorkSession.FirstOrDefaultAsync(s => s.Id == sessionId && s.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        if (session is null) return;

        var currentWatermark = session.SummaryThroughTurnIndex ?? 0;
        var storedBindings = SessionSummarySourceBindings.Parse(session.SummarySourceBindingJson).ToDictionary(b => b.Turn);

        var (freshBindings, manifestsByRunId) = await LoadFreshBindingsAsync(teamId, olderTurns, cancellationToken).ConfigureAwait(false);

        // A previously-folded turn whose bound source no longer matches what is CURRENTLY effective — a rerun that
        // has since won, a mutated result, or a newly recorded assessment. A turn with NO stored binding (a legacy
        // row from before this column existed) is left alone — unknown provenance, never a forced mass re-summarize.
        var dirtyTurns = olderTurns.Where(t => t.Turn <= currentWatermark && IsDirty(freshBindings[t.Turn!.Value], storedBindings.GetValueOrDefault(t.Turn!.Value))).ToList();

        if (targetWatermark <= currentWatermark && dirtyTurns.Count == 0) return;   // fully caught up, and nothing changed underneath either

        var (foldTurns, baseSummary) = PlanFold(olderTurns, currentWatermark, dirtyTurns, session);

        if (foldTurns.Count == 0) return;   // throttled, and no turn newly scrolled out either — nothing to do this call

        var distilled = await TryDistillAsync(teamId, baseSummary, foldTurns, manifestsByRunId, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(distilled))
        {
            // Fail-open: no model / LLM error — Summary (and its source binding) are left unchanged, but the GAP must
            // not stay silent. The oldest un-folded turn is the earliest one this failure leaves behind; never
            // overwritten by a LATER failure's (necessarily newer) gap — the oldest gap is the one a reader needs to know about.
            session.SummaryStaleSinceTurn ??= foldTurns[0].Turn;
            return;
        }

        session.Summary = distilled.Trim();
        session.SummaryThroughTurnIndex = targetWatermark;
        session.SummaryStaleSinceTurn = null;
        session.SummarySourceBindingJson = SerializeBindings(olderTurns, foldTurns, freshBindings, storedBindings);
    }

    /// <summary>
    /// The turns OLDER than the recent verbatim window = ALL BUT the most recent <see cref="SessionContextBuilder.MaxTurns"/>
    /// turns (by turn index). ROW-based (Skip), NOT value-based (latest − MaxTurns), so it stays exactly complementary
    /// to <c>SessionContextBuilder.BuildAsync</c>'s count-based Take(MaxTurns) window even when turn indices are
    /// non-contiguous (a gap would otherwise leave a turn in NEITHER the summary nor the window, or in BOTH). Empty ⇒
    /// the whole thread fits the window ⇒ no summary needed (byte-identical short-thread digest). Newest-first.
    /// </summary>
    private async Task<List<TurnRow>> LoadOlderTurnsAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        // The session's WHOLE narrow lineage (a rerun/replay attempt inherits the SessionId with a NULL turn index) —
        // each turn's EFFECTIVE attempt is resolved before windowing. JSON roots never cross into this model path.
        var lineage = await _turns.ListAsync(sessionId, teamId, cancellationToken).ConfigureAwait(false);

        return lineage.GroupBy(r => r.RootRunId ?? r.Id)
            .Where(g => g.Any(r => r.SessionTurnIndex != null))
            .Select(g => new { Turn = g.First(r => r.SessionTurnIndex != null).SessionTurnIndex, EffectiveId = SessionTurnAttempts.ResolveEffectiveId(g.Select(r => new SessionTurnAttempts.AttemptRow(r.Id, r.Status, r.CreatedDate))) })
            .Join(lineage, t => t.EffectiveId, r => r.Id, (t, r) => new TurnRow(r.Id, t.Turn, r.Status.ToString(), r.Goal, r.Result, r.LegacyBranch))
            .OrderByDescending(t => t.Turn)
            .Skip(SessionContextBuilder.MaxTurns)
            .ToList();
    }

    /// <summary>
    /// Every older turn's CURRENT source binding — cheap (ids + a fingerprint, never the JSON roots) — computed for
    /// the WHOLE older-turns span (not just what this call folds) so an already-folded turn's drift can be detected
    /// even when the watermark itself has nothing new to fold, AND so a turn this column never bound before (a
    /// legacy row) can be backfilled by <see cref="SerializeBindings"/> rather than staying unbound forever. The
    /// manifests are returned too — <see cref="TryDistillAsync"/> reuses this SAME (already-loaded) lookup.
    /// </summary>
    private async Task<(Dictionary<int, SessionSummarySourceBinding> FreshBindings, IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>> ManifestsByRunId)> LoadFreshBindingsAsync(Guid teamId, IReadOnlyList<TurnRow> olderTurns, CancellationToken cancellationToken)
    {
        var assessmentIdsByRunId = await LoadLatestAssessmentIdsAsync(teamId, olderTurns.Select(t => t.Id).ToList(), cancellationToken).ConfigureAwait(false);
        var manifestsByRunId = await _manifests.ListForWorkflowRunsAsync(olderTurns.Select(t => t.Id).ToList(), teamId, cancellationToken).ConfigureAwait(false);
        var freshBindings = olderTurns.ToDictionary(t => t.Turn!.Value, t => BuildBinding(t, assessmentIdsByRunId, manifestsByRunId));

        return (freshBindings, manifestsByRunId);
    }

    /// <summary>
    /// Which older turns to fold THIS call, and the existing summary to fold them onto. A dirty rebuild re-prompts
    /// the WHOLE older-turns span (the incremental base is no longer trustworthy once ANY already-folded turn's
    /// bound source changed underneath it) — EXCEPT that expensive rebuild is retried at most once per known
    /// failure: once a prior attempt already failed (<c>SummaryStaleSinceTurn</c> still set from it), this falls
    /// back to the cheap incremental fold instead of re-paying for the whole span on every launch under a sustained
    /// provider outage. Re-arms the next time any fold succeeds and clears the flag.
    /// </summary>
    private static (List<TurnRow> FoldTurns, string? BaseSummary) PlanFold(IReadOnlyList<TurnRow> olderTurns, int currentWatermark, IReadOnlyList<TurnRow> dirtyTurns, WorkSession session)
    {
        var throttleFullRebuild = dirtyTurns.Count > 0 && session.SummaryStaleSinceTurn is not null;

        if (dirtyTurns.Count > 0 && !throttleFullRebuild)
            return (olderTurns.OrderBy(t => t.Turn).ToList(), null);   // full rebuild (this also naturally folds in any turn that scrolled out in the same pass)

        // Fold ONLY the turns newly scrolled out (above the current watermark), oldest-first — incremental, not a re-summarize.
        return (olderTurns.Where(t => t.Turn > currentWatermark).OrderBy(t => t.Turn).ToList(), session.Summary);
    }

    /// <summary>
    /// Every older turn's binding, written whole: FRESH for a turn actually folded into the prose this call (its
    /// content IS now what fresh describes); else its own STORED binding when one exists (preserves a still-dirty
    /// turn's drift flag across a throttled call that folded past it without re-deriving it); else FRESH as a
    /// one-time backfill for a turn this column never bound before (a legacy row — never a forced mass re-summarize,
    /// just adopting its current state as the baseline for FUTURE drift detection).
    /// </summary>
    private static string SerializeBindings(IReadOnlyList<TurnRow> olderTurns, IReadOnlyList<TurnRow> foldTurns, IReadOnlyDictionary<int, SessionSummarySourceBinding> freshBindings, IReadOnlyDictionary<int, SessionSummarySourceBinding> storedBindings)
    {
        var foldedTurnNumbers = foldTurns.Select(t => t.Turn!.Value).ToHashSet();

        return SessionSummarySourceBindings.Serialize(
            olderTurns.Select(t => foldedTurnNumbers.Contains(t.Turn!.Value) ? freshBindings[t.Turn!.Value] : storedBindings.GetValueOrDefault(t.Turn!.Value) ?? freshBindings[t.Turn!.Value])
                .OrderBy(b => b.Turn).ToList());
    }

    /// <summary>The latest <c>CompletionAssessmentRecord.Id</c> per <c>WorkflowRunId</c>, for the given (already-narrow) run ids — an id-only read, never the assessment body.</summary>
    private async Task<IReadOnlyDictionary<Guid, Guid>> LoadLatestAssessmentIdsAsync(Guid teamId, IReadOnlyList<Guid> runIds, CancellationToken cancellationToken)
    {
        if (runIds.Count == 0) return new Dictionary<Guid, Guid>();

        return (await _db.CompletionAssessmentRecord.AsNoTracking()
            .Where(a => a.TeamId == teamId && runIds.Contains(a.WorkflowRunId))
            .OrderBy(a => a.CreatedDate).ThenBy(a => a.Id)   // a tied CreatedDate must still resolve to ONE stable "latest" (never a perpetually-dirty flip-flop)
            .Select(a => new { a.WorkflowRunId, a.Id })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(a => a.WorkflowRunId)
            .ToDictionary(g => g.Key, g => g.Last().Id);
    }

    /// <summary>This turn's CURRENT source binding — its effective run id, a fingerprint of what would be folded (the SAME manifest-preferred branch <see cref="BuildUserPrompt"/> renders, via <see cref="ResolvedBranch"/> — never the raw <see cref="TurnRow.LegacyBranch"/> column alone), and its latest assessment id (null = none recorded). Internal so a test can pin the fingerprint's determinism without a DB round-trip.</summary>
    internal static SessionSummarySourceBinding BuildBinding(TurnRow t, IReadOnlyDictionary<Guid, Guid> assessmentIdsByRunId, IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>> manifestsByRunId) => new()
    {
        Turn = t.Turn!.Value,
        EffectiveRunId = t.Id,
        ResultFingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{t.Status}|{t.Goal}|{t.Result}|{ResolvedBranch(t, manifestsByRunId)}"))),
        AssessmentId = assessmentIdsByRunId.TryGetValue(t.Id, out var id) ? id : null,
    };

    /// <summary>The branch <see cref="BuildUserPrompt"/> folds for this turn — the run's manifest-preferred branch (I2) when one resolves, else the raw legacy <c>OutputsJson.branch</c> compatibility leaf. Shared by <see cref="BuildBinding"/>'s fingerprint so the two can never drift apart (a manifest resolving AFTER the fold, or to a branch the legacy leaf disagrees with, must still register as a content change).</summary>
    private static string? ResolvedBranch(TurnRow t, IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>? manifestsByRunId) =>
        SessionManifestBranches.ResolveSingleRepoBranch(manifestsByRunId?.GetValueOrDefault(t.Id))?.Branch ?? t.LegacyBranch;

    /// <summary>True when a previously-bound turn's CURRENT source no longer matches what the summary was built from — its effective attempt changed (a rerun won), its content changed, or its recorded assessment changed. A turn with no stored binding is never dirty (unknown legacy provenance, not a forced refresh). Internal so a test can pin each dirty dimension directly.</summary>
    internal static bool IsDirty(SessionSummarySourceBinding fresh, SessionSummarySourceBinding? stored) =>
        stored is not null && (fresh.EffectiveRunId != stored.EffectiveRunId || fresh.ResultFingerprint != stored.ResultFingerprint || fresh.AssessmentId != stored.AssessmentId);

    /// <summary>Distill the existing summary + the newly scrolled-out turns into an updated summary. Returns null (fail-open) when no provider/model is available or the LLM call fails. Internal so the real-model eval can drive the live distillation directly (DB-free), pinning that the summary actually preserves older turns.</summary>
    internal async Task<string?> TryDistillAsync(Guid teamId, string? existingSummary, IReadOnlyList<TurnRow> newTurns, IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>> manifestsByRunId, CancellationToken cancellationToken)
    {
        // The WHOLE resolve → select → complete path is fail-open: model resolution DECRYPTS the credential
        // (SelectAsync can throw CryptographicException on a corrupt / rotated / cross-key-ring key), and the LLM call
        // can throw — ANY of these must leave the summary unchanged rather than fail the launch (the contract).
        try
        {
            var resolved = await InProcessTextModel.ResolveAsync(_clientRegistry, _modelSelector, teamId, pinnedModel: null, cancellationToken).ConfigureAwait(false);

            if (resolved is not { } model) return null;   // no registered provider has a credentialed team model — fail open

            var completion = await model.Client.CompleteAsync(new LLMCompletionRequest
            {
                Model = model.Pick.ModelId,
                Credential = model.Pick.Credential,
                SystemPrompt = SystemPrompt,
                UserPrompt = BuildUserPrompt(existingSummary, newTurns, manifestsByRunId),
                MaxOutputTokens = 1024,
                Temperature = 0.2,
            }, cancellationToken).ConfigureAwait(false);

            return completion.Text;
        }
        catch (Exception ex)
        {
            // Best-effort: a summarization failure (credential decrypt, model resolution, or the LLM call) must never
            // fail the launch — the digest falls back to the recent window.
            _logger.LogWarning(ex, "Session summary distillation failed for team {TeamId}; leaving the rolling summary unchanged", teamId);
            return null;
        }
    }

    /// <summary>The distillation prompt: the running summary so far + the next older turns to fold in. Internal so a test can pin the framing without a real LLM round-trip.</summary>
    internal static string BuildUserPrompt(string? existingSummary, IReadOnlyList<TurnRow> newTurns, IReadOnlyDictionary<Guid, IReadOnlyList<PublishManifest>>? manifestsByRunId = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            sb.AppendLine("Summary so far:");
            sb.AppendLine(existingSummary.Trim());
            sb.AppendLine();
            sb.AppendLine("Fold these earlier turns into the summary above:");
        }
        else
        {
            sb.AppendLine("Summarise these earlier turns of the work thread:");
        }

        foreach (var t in newTurns)
        {
            sb.AppendLine();
            sb.AppendLine($"Turn {t.Turn} ({t.Status}):");

            if (t.Goal != null) sb.AppendLine($"  Asked: {SessionTurnText.Clip(t.Goal)}");

            if (t.Result != null) sb.AppendLine($"  Result: {SessionTurnText.Clip(t.Result)}");

            var branch = ResolvedBranch(t, manifestsByRunId);
            if (branch != null) sb.AppendLine($"  Produced branch: {branch}");
        }

        return sb.ToString().TrimEnd();
    }

    private const string SystemPrompt =
        "You maintain a running summary of a multi-turn software work thread. Given the summary so far (if any) and " +
        "the next earlier turns, produce an UPDATED concise summary that preserves the key decisions, outcomes, " +
        "produced branches, and still-open threads — enough for a later turn to recall the early work without redoing " +
        "it. Keep it tight (a few short paragraphs at most). Output ONLY the updated summary prose, no preamble.";

    /// <summary>One older turn's clean fields for distillation (the same source-of-truth the digest reads).</summary>
    internal sealed record TurnRow(Guid Id, int? Turn, string Status, string? Goal, string? Result, string? LegacyBranch);
}
