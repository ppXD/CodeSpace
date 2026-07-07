using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.Review;

/// <summary>
/// Reads a run's REVIEWER agent runs (the S8/D① runs whose cell key carries the <c>#review</c> / <c>#plan-review</c>
/// suffix) and parses each landed verdict — the ONE place a projection turns a reviewer row into a render-ready
/// <see cref="JournalReviewVerdict"/>, shared by the verdict timeline source (the REVIEW beat), the journal facts
/// source, and the producer-card join. Re-uses the production <see cref="AgentReviewRunner.ParseVerdict"/> so a
/// projection can never disagree with the executor about what a reviewer concluded. DELIBERATELY event-log-free:
/// the verdict is read off the reviewer's durable RESULT, so it surfaces identically whether or not the harness
/// emitted a final-summary event (codex-cli does not — the miss that hid a real run's verdict). An IN-FLIGHT
/// reviewer (Queued/Running) yields a verdict-LESS row — the "reviewer inspecting…" beat, upgraded in place when
/// the verdict lands (same event id); a terminal non-succeeded / off-contract run yields nothing (fail-open — the
/// model-critic fallback's own beat takes over). READ-ONLY, one narrow batch query.
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
            .Select(r => new ReviewerSlice(r.Id, r.IterationKey, r.NodeId, r.Status, r.ResultJson, r.CreatedDate, r.CompletedAt, r.TaskJson))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (rows.Count == 0) return Array.Empty<ReviewerVerdictRow>();

        return rows
            .Select(ToRow)
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

    /// <summary>One reviewer row → its parsed verdict; an IN-FLIGHT row (Queued/Running) keeps its beat with a NULL verdict ("reviewer inspecting…", upgraded in place when it lands); a terminal non-succeeded / off-contract run yields null — fail-open, mirroring the executor's ladder.</summary>
    private static ReviewerVerdictRow? ToRow(ReviewerSlice r)
    {
        var scope = r.IterationKey.EndsWith(AgentPlanReviewer.IterationKey, StringComparison.Ordinal) ? JournalReviewVerdict.PlanScope : JournalReviewVerdict.OutputScope;

        if (r.Status is AgentRunStatus.Queued or AgentRunStatus.Running)
            return new ReviewerVerdictRow(r.Id, r.IterationKey, r.NodeId, r.CreatedDate, r.CreatedDate, scope, Verdict: null);

        if (r.Status != AgentRunStatus.Succeeded || string.IsNullOrEmpty(r.ResultJson)) return null;

        var verdict = AgentReviewRunner.ParseVerdict(TryReadSummary(r.ResultJson));

        if (verdict.Failed) return null;

        return new ReviewerVerdictRow(r.Id, r.IterationKey, r.NodeId, r.CreatedDate, r.CompletedAt ?? r.CreatedDate, scope, new JournalReviewVerdict
        {
            Approved = verdict.Approved,
            Rationale = verdict.Rationale,
            Issues = verdict.Issues.Select(i => i.ToString()).ToList(),
            ReviewerRunId = r.Id,
            ReviewerHarness = TryReadHarness(r.TaskJson),
            Scope = scope,
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

    private sealed record ReviewerSlice(Guid Id, string IterationKey, string? NodeId, AgentRunStatus Status, string? ResultJson, DateTimeOffset CreatedDate, DateTimeOffset? CompletedAt, string? TaskJson);
    private sealed record SummarySlice(string? Summary);
    private sealed record HarnessSlice(string? Harness);
}

/// <summary>One reviewer run's beat: the reviewer run id (the beat's identity — the same event id in flight and landed), its cell key (the producer join key) + node, its staging time (latest-wins per producer) and completion time (the verdict beat's timestamp; = staging while in flight), the scope (<c>plan</c>/<c>output</c>, known from the key before any verdict), and the render-ready verdict — NULL while the reviewer is still inspecting.</summary>
public sealed record ReviewerVerdictRow(Guid ReviewerRunId, string IterationKey, string? NodeId, DateTimeOffset CreatedAt, DateTimeOffset CompletedAt, string Scope, JournalReviewVerdict? Verdict);
