using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Default <see cref="ISessionSummarizer"/>. Folds the turns that have scrolled out of the digest's recent window
/// (those above the summary watermark) into <c>WorkSession.Summary</c> via a plain-text LLM distillation, resolving
/// the model from the team's pool the SAME way <c>LlmWorkflowPlanner</c> does. Incremental (only the newly scrolled-out
/// turns are folded into the existing summary) and FAIL-OPEN (no pool model / LLM error ⇒ the summary is left
/// unchanged and the launch proceeds with the recent window only).
/// </summary>
public sealed class SessionSummarizer : ISessionSummarizer, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILLMClientRegistry _clientRegistry;
    private readonly IModelPoolSelector _modelSelector;
    private readonly ILogger<SessionSummarizer> _logger;

    public SessionSummarizer(CodeSpaceDbContext db, ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector, ILogger<SessionSummarizer> logger)
    {
        _db = db;
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
        _logger = logger;
    }

    public async Task EnsureSummaryUpToDateAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken)
    {
        // The turns OLDER than the recent verbatim window = ALL BUT the most recent MaxTurns turns (by turn index).
        // ROW-based (Skip), NOT value-based (latest − MaxTurns), so it stays exactly complementary to BuildAsync's
        // count-based Take(MaxTurns) window even when turn indices are non-contiguous (a gap would otherwise leave a
        // turn in NEITHER the summary nor the window, or in BOTH). Empty ⇒ the whole thread fits the window ⇒ no
        // summary needed (byte-identical short-thread digest). Newest-first; the watermark is the newest older turn.
        var olderTurns = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.TeamId == teamId && r.SessionTurnIndex != null)
            .OrderByDescending(r => r.SessionTurnIndex)
            .Skip(SessionContextBuilder.MaxTurns)
            .Select(r => new TurnRow(r.SessionTurnIndex, r.Status.ToString(), r.OutputsJson, r.RunRequest.NormalizedPayloadJson))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (olderTurns.Count == 0) return;

        var targetWatermark = olderTurns[0].Turn!.Value;   // newest of the older turns (first in desc order)

        // TRACKED load — the Summary write stages on the shared request-scoped DbContext, committing atomically with the run.
        var session = await _db.WorkSession.FirstOrDefaultAsync(s => s.Id == sessionId && s.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        if (session is null) return;

        var currentWatermark = session.SummaryThroughTurnIndex ?? 0;

        if (targetWatermark <= currentWatermark) return;   // the summary already covers the older turns

        // Fold ONLY the turns newly scrolled out (above the current watermark), oldest-first — incremental, not a re-summarize.
        var newTurns = olderTurns.Where(t => t.Turn > currentWatermark).OrderBy(t => t.Turn).ToList();

        if (newTurns.Count == 0) return;

        var distilled = await TryDistillAsync(teamId, session.Summary, newTurns, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(distilled)) return;   // fail-open: no model / LLM error — leave the summary unchanged

        session.Summary = distilled.Trim();
        session.SummaryThroughTurnIndex = targetWatermark;
    }

    /// <summary>Distill the existing summary + the newly scrolled-out turns into an updated summary. Returns null (fail-open) when no provider/model is available or the LLM call fails. Internal so the real-model eval can drive the live distillation directly (DB-free), pinning that the summary actually preserves older turns.</summary>
    internal async Task<string?> TryDistillAsync(Guid teamId, string? existingSummary, IReadOnlyList<TurnRow> newTurns, CancellationToken cancellationToken)
    {
        // The WHOLE resolve → select → complete path is fail-open: model resolution DECRYPTS the credential
        // (SelectAsync can throw CryptographicException on a corrupt / rotated / cross-key-ring key), and the LLM call
        // can throw — ANY of these must leave the summary unchanged rather than fail the launch (the contract).
        try
        {
            var client = _clientRegistry.All.FirstOrDefault();

            if (client is null) return null;

            var pick = await _modelSelector.SelectAsync(teamId, client.Provider, allowedModels: null, pinnedModel: null, cancellationToken).ConfigureAwait(false);

            if (pick is null) return null;   // no credentialed model in the team's pool — fail open

            var completion = await client.CompleteAsync(new LLMCompletionRequest
            {
                Model = pick.ModelId,
                Credential = pick.Credential,
                SystemPrompt = SystemPrompt,
                UserPrompt = BuildUserPrompt(existingSummary, newTurns),
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
    internal static string BuildUserPrompt(string? existingSummary, IReadOnlyList<TurnRow> newTurns)
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

            var goal = SessionTurnText.ReadString(t.Payload, "goal");
            if (goal != null) sb.AppendLine($"  Asked: {SessionTurnText.Clip(goal)}");

            var result = SessionTurnText.ReadResult(t.OutputsJson);
            if (result != null) sb.AppendLine($"  Result: {SessionTurnText.Clip(result)}");

            var branch = SessionTurnText.ReadString(t.OutputsJson, "branch");
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
    internal sealed record TurnRow(int? Turn, string Status, string OutputsJson, string Payload);
}
