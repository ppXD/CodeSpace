using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Reads a run's REVIEWER agent runs (the S8/D① runs whose cell key carries the <c>#review</c> / <c>#plan-review</c>
/// suffix) and parses each landed verdict — the ONE place the journal turns a reviewer row into a render-ready
/// <see cref="JournalReviewVerdict"/>, shared by the verdict-beat facts source and the producer-card join. Re-uses the
/// production <see cref="AgentReviewRunner.ParseVerdict"/> so the journal can never disagree with the executor about
/// what a reviewer concluded. A reviewer run still in flight, non-succeeded, or off-contract yields NO verdict (the
/// describer's generic review step stands bare). READ-ONLY, two narrow batch queries.
/// </summary>
public sealed class ReviewerVerdictReader : IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public ReviewerVerdictReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<IReadOnlyList<ReviewerVerdictRow>> ReadForRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.WorkflowRunId == runId
                        && (r.IterationKey.EndsWith(AgentOutputReviewer.IterationKeySuffix) || r.IterationKey.EndsWith(AgentPlanReviewer.IterationKey)))
            .Select(r => new ReviewerSlice(r.Id, r.IterationKey, r.Status, r.ResultJson, r.CreatedDate, r.TaskJson))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return Array.Empty<ReviewerVerdictRow>();

        var finalEventByRun = await FinalSummaryEventIdsAsync(rows.Select(r => r.Id).ToList(), cancellationToken).ConfigureAwait(false);

        return rows
            .Select(r => ToRow(r, finalEventByRun.GetValueOrDefault(r.Id)))
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();
    }

    /// <summary>The PRODUCERS' cell keys for a set of agent run ids — the join side of the card chip: an output reviewer's key is the producer's key + <c>#review</c>. One narrow team-scoped batch query.</summary>
    public async Task<IReadOnlyDictionary<Guid, string>> ProducerKeysAsync(IReadOnlyCollection<Guid> agentRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (agentRunIds.Count == 0) return EmptyKeys;

        var ids = agentRunIds as IReadOnlyList<Guid> ?? agentRunIds.ToList();

        return await _db.AgentRun.AsNoTracking()
            .Where(r => r.TeamId == teamId && ids.Contains(r.Id))
            .Select(r => new { r.Id, r.IterationKey })
            .ToDictionaryAsync(r => r.Id, r => r.IterationKey, cancellationToken).ConfigureAwait(false);
    }

    private static readonly IReadOnlyDictionary<Guid, string> EmptyKeys = new Dictionary<Guid, string>();

    /// <summary>Each reviewer run's FINAL-SUMMARY event id (the verdict beat the facts key by) — the LATEST when a re-attach ever produced more than one.</summary>
    private async Task<IReadOnlyDictionary<Guid, Guid>> FinalSummaryEventIdsAsync(IReadOnlyList<Guid> reviewerIds, CancellationToken cancellationToken)
    {
        var events = await _db.AgentRunEvent.AsNoTracking()
            .Where(e => reviewerIds.Contains(e.AgentRunId) && e.Kind == AgentEventKind.FinalSummary)
            .Select(e => new { e.Id, e.AgentRunId, e.Sequence })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return events
            .GroupBy(e => e.AgentRunId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.Sequence).First().Id);
    }

    /// <summary>One reviewer row → its parsed verdict, or null when it carries none yet (in flight / failed / off-contract) — fail-open, mirroring the executor's ladder.</summary>
    private static ReviewerVerdictRow? ToRow(ReviewerSlice r, Guid finalSummaryEventId)
    {
        if (r.Status != AgentRunStatus.Succeeded || string.IsNullOrEmpty(r.ResultJson)) return null;

        var verdict = AgentReviewRunner.ParseVerdict(TryReadSummary(r.ResultJson));

        if (verdict.Failed) return null;

        return new ReviewerVerdictRow(r.IterationKey, r.CreatedDate, finalSummaryEventId, new JournalReviewVerdict
        {
            Approved = verdict.Approved,
            Rationale = verdict.Rationale,
            Issues = verdict.Issues.Select(i => i.ToString()).ToList(),
            ReviewerRunId = r.Id,
            ReviewerHarness = TryReadHarness(r.TaskJson),
            Scope = r.IterationKey.EndsWith(AgentPlanReviewer.IterationKey, StringComparison.Ordinal) ? JournalReviewVerdict.PlanScope : JournalReviewVerdict.OutputScope,
        });
    }

    private static string? TryReadSummary(string resultJson)
    {
        try { return JsonSerializer.Deserialize<SummarySlice>(resultJson, AgentJson.Options)?.Summary; }
        catch (JsonException) { return null; }
    }

    private static string? TryReadHarness(string? taskJson)
    {
        if (string.IsNullOrEmpty(taskJson)) return null;

        try
        {
            var harness = JsonSerializer.Deserialize<HarnessSlice>(taskJson, AgentJson.Options)?.Harness;
            return string.IsNullOrWhiteSpace(harness) ? null : harness;
        }
        catch (JsonException) { return null; }
    }

    private sealed record ReviewerSlice(Guid Id, string IterationKey, AgentRunStatus Status, string? ResultJson, DateTimeOffset CreatedDate, string? TaskJson);
    private sealed record SummarySlice(string? Summary);
    private sealed record HarnessSlice(string? Harness);
}

/// <summary>One landed reviewer verdict: the reviewer run's cell key (its producer join key), its staging time (latest-wins per producer), the verdict-beat event to hang facts on, and the render-ready verdict.</summary>
public sealed record ReviewerVerdictRow(string IterationKey, DateTimeOffset CreatedAt, Guid FinalSummaryEventId, JournalReviewVerdict Verdict);
