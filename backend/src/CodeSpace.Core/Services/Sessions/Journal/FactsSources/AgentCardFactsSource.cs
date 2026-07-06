using CodeSpace.Core.Services.Agents.Review;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Tasks.Phases;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Phases;

namespace CodeSpace.Core.Services.Sessions.Journal.FactsSources;

/// <summary>
/// Enriches each supervisor decision that STAGED agents (a spawn's fan-out, a retry's re-run) with the agents it launched
/// — render-ready cards keyed by the decision's timeline event id, so the walk hangs them off the same step the supervisor
/// describer produced. Reads each decision's staged agent ids off its outcome (<see cref="SupervisorOutcome.ReadStagedAgentRunIds"/>)
/// and builds cards from the SHARED <see cref="AgentMetricsReader"/> — the same ground-truth numbers the phase board and
/// the room card read, in ONE batched query for the whole run. A decision that staged none contributes nothing; a run's
/// re-spawn waves need no special handling (each wave is its own later decision step carrying its own cards).
/// </summary>
public sealed class AgentCardFactsSource : IJournalFactsSource
{
    private readonly ISupervisorDecisionLog _decisions;
    private readonly AgentMetricsReader _metrics;
    private readonly ReviewerVerdictReader _verdicts;

    public AgentCardFactsSource(ISupervisorDecisionLog decisions, AgentMetricsReader metrics, ReviewerVerdictReader verdicts)
    {
        _decisions = decisions;
        _metrics = metrics;
        _verdicts = verdicts;
    }

    public async Task<IReadOnlyDictionary<string, JournalStepFacts>> GatherAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var tape = await _decisions.GetForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var stagedByDecision = tape
            .Select(d => (Decision: d, AgentIds: SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson)))
            .Where(x => x.AgentIds.Count > 0)
            .ToList();

        if (stagedByDecision.Count == 0) return EmptyFacts;

        var allAgentIds = stagedByDecision.SelectMany(x => x.AgentIds).Distinct().ToList();
        var metrics = await _metrics.ReadAsync(teamId, allAgentIds, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        // The SAME allocation (short subtask label) + ledger compact (git-truth files) the room card reads, folded from
        // the already-loaded tape — so a journal card can't disagree with the room card for the same agent.
        var allocation = SupervisorAgentAllocation.Map(tape);
        var results = SupervisorAgentAllocation.ResultsById(tape);
        var reviewByAgent = await ReviewByProducerAsync(runId, teamId, allAgentIds, cancellationToken).ConfigureAwait(false);

        var facts = new Dictionary<string, JournalStepFacts>();

        foreach (var (decision, agentIds) in stagedByDecision)
        {
            var cards = agentIds
                .Where(metrics.ContainsKey)   // an id whose row isn't the team's / not yet readable is skipped, never fabricated
                .Select(id => ToCard(id, metrics[id], allocation.GetValueOrDefault(id), results.GetValueOrDefault(id), reviewByAgent.GetValueOrDefault(id)))
                .ToList();

            if (cards.Count > 0)
                facts[SupervisorDecisionTimelineMap.EventId(decision)] = new JournalStepFacts { Agents = cards };
        }

        return facts;
    }

    /// <summary>
    /// The LATEST landed reviewer verdict per staged agent — the card's "✓ reviewed / ⚠ flagged" chip. An output
    /// reviewer's cell key is its producer's key + <c>#review</c>, so the join is a suffix strip against the producers'
    /// own keys (batch-read); multiple review rounds (the S6 loop re-reviews each revision) collapse latest-wins. Plan
    /// reviews (<c>#plan-review</c>, no producer agent) never match a card — they surface as their own REVIEW beat.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, JournalReviewVerdict>> ReviewByProducerAsync(Guid runId, Guid teamId, IReadOnlyList<Guid> agentIds, CancellationToken cancellationToken)
    {
        var verdictRows = await _verdicts.ReadForRunAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var outputVerdicts = verdictRows.Where(v => v.Verdict.Scope == JournalReviewVerdict.OutputScope).ToList();

        if (outputVerdicts.Count == 0) return EmptyReviews;

        var producerKeys = await _verdicts.ProducerKeysAsync(agentIds, teamId, cancellationToken).ConfigureAwait(false);

        var latestByKey = outputVerdicts
            .GroupBy(v => v.IterationKey)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.CreatedAt).First().Verdict);

        return producerKeys
            .Where(p => latestByKey.ContainsKey(AgentOutputReviewer.ReviewIterationKey(p.Value)))
            .ToDictionary(p => p.Key, p => latestByKey[AgentOutputReviewer.ReviewIterationKey(p.Value)]);
    }

    private static readonly IReadOnlyDictionary<Guid, JournalReviewVerdict> EmptyReviews = new Dictionary<Guid, JournalReviewVerdict>();

    /// <summary>
    /// Map the shared metrics projection to a journal card — the ground-truth (status · tokens · duration · cost · tool
    /// count from the metrics reader). The LABEL prefers the subtask's stable ID (the SAME slug the deferred "waiting on
    /// {id}" labels use, so a card and its dependents correlate by name), else the semantic role, else the planned subtask
    /// title, else the raw instruction, else a neutral word — a supervisor card reads "spec-and-analyze" (matching its
    /// dependency labels) with the human title carried as <see cref="JournalAgentCard.AssignedSubtask"/> for the hover +
    /// drawer, while a map/flow agent (no allocation → no id) keeps its goal. FILES prefer the ledger COMPACT's git-truth
    /// changed files (the same source the room reads, present even when the agent's own result row didn't fold a
    /// changed-file list — e.g. codex-cli), falling back to the metrics reader; the per-file diffstat rows use the metrics
    /// reader when it has them, else the compact's path-only list. Total tokens is null unless the agent reported usage.
    /// </summary>
    internal static JournalAgentCard ToCard(Guid agentRunId, AgentRunMetrics m, AgentAllocation? allocation, SupervisorAgentResult? compact, JournalReviewVerdict? review = null)
    {
        var compactFiles = compact?.ChangedFiles ?? Array.Empty<string>();

        return new JournalAgentCard
        {
            AgentRunId = agentRunId,
            Label = FirstNonBlank(allocation?.SubtaskId, allocation?.Role, allocation?.SubtaskTitle, m.Goal) ?? "Agent",
            AssignedSubtask = allocation?.SubtaskTitle,
            Status = m.Status,
            // The failure cause on a NON-succeeded card — already gated to null on a succeeded run + secret-redacted at
            // the write path, so a green card never shows a stray error and the failed card names WHY (an LLM 4xx, etc.).
            Error = m.Error,
            Model = m.Model,
            Harness = m.Harness,
            DurationMs = m.DurationMs,
            Tokens = m.InputTokens is null && m.OutputTokens is null ? null : (m.InputTokens ?? 0) + (m.OutputTokens ?? 0),
            ToolCount = m.ToolCount,
            CostUsd = m.CostUsd,
            FilesChanged = compactFiles.Count > 0 ? compactFiles.Count : m.FilesChanged,
            Files = m.ChangedFileStats.Count > 0 ? m.ChangedFileStats : compactFiles.Select(p => new FileDiffStat(p, null, null)).ToList(),
            Resumed = m.Resumed,
            Review = review,
        };
    }

    /// <summary>The first non-blank value, trimmed — the label picks role → subtask title → instruction, treating an empty/whitespace field as absent so the card never shows a blank name.</summary>
    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static readonly IReadOnlyDictionary<string, JournalStepFacts> EmptyFacts = new Dictionary<string, JournalStepFacts>();
}
