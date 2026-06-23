using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Pure mapping from ONE <c>agent_run_event</c> harness log row to a narrative timeline event. Only the
/// NARRATIVE-worthy kinds (<see cref="Narrative"/>: file edits, test output, errors, warnings, the final summary)
/// become events — the verbose chatter (assistant text, reasoning, tool/command lines, queued/started/completed
/// lifecycle) stays in the per-agent timeline + the Trace tab, not the run story line. Extracted from the source so
/// the kind set + the per-event shape are unit-testable without a database.
/// </summary>
public static class AgentEventTimelineMap
{
    /// <summary>The agent-events source's provenance key — stamped on every event this mapper emits.</summary>
    public const string Key = "agent-events";

    /// <summary>The narrative-worthy harness event kinds — the ONLY kinds that surface on the run story line. Also the source's SQL filter (kind IN …), so a verbose run doesn't flood the timeline.</summary>
    public static readonly AgentEventKind[] Narrative =
    {
        AgentEventKind.FileChanged,
        AgentEventKind.TestOutput,
        AgentEventKind.Error,
        AgentEventKind.Warning,
        AgentEventKind.FinalSummary,
    };

    /// <summary>The headline cap — a long FinalSummary (the whole report) is clamped here; the full text stays in the agent's own timeline + Trace.</summary>
    private const int MaxTitle = 200;

    public static RunTimelineEvent ToEvent(AgentRunEvent e, IReadOnlyDictionary<Guid, string?> nodeByAgent)
    {
        var headline = string.IsNullOrWhiteSpace(e.Text) ? e.Kind.ToString() : e.Text;

        return new RunTimelineEvent
        {
            Id = $"agent-{e.Id:N}",
            Kind = $"agent.{e.Kind}",
            Title = Truncate(headline, MaxTitle),
            Severity = SeverityFor(e.Kind),
            Level = LevelFor(e.Kind),
            OccurredAt = e.OccurredAt,
            Order = e.Sequence,
            NodeId = nodeByAgent.TryGetValue(e.AgentRunId, out var node) ? node : null,
            AgentRunId = e.AgentRunId.ToString(),
            SourceKey = Key,
        };
    }

    // An agent's ERROR and its FINAL SUMMARY are story milestones (the failure + the conclusion the operator reads);
    // its file edits / test output / warnings are DETAIL the wave + the agent's own terminal already carry, so they
    // fold away on the run story line.
    private static TimelineLevel LevelFor(AgentEventKind kind) => kind switch
    {
        AgentEventKind.Error => TimelineLevel.Milestone,
        AgentEventKind.FinalSummary => TimelineLevel.Milestone,
        _ => TimelineLevel.Detail,
    };

    // TestOutput is deliberately Info: its pass/fail detail lives in the event Text, and reading a failure marker out
    // of the harness-specific DataJson to escalate to Warning/Error would couple this run-neutral source to one
    // harness's payload shape. Per-result severity is a future refinement once a stable cross-harness marker exists.
    private static TimelineSeverity SeverityFor(AgentEventKind kind) => kind switch
    {
        AgentEventKind.Error => TimelineSeverity.Error,
        AgentEventKind.Warning => TimelineSeverity.Warning,
        _ => TimelineSeverity.Info,
    };

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;

        // Don't cut INSIDE a surrogate pair — an astral char (emoji / CJK Ext-B that legitimately appears in a
        // free-form summary) split mid-pair leaves a lone surrogate that renders as U+FFFD. Back off one unit,
        // mirroring WorkSessionService.SanitizeTitle.
        var cut = char.IsHighSurrogate(text[max - 1]) ? max - 1 : max;

        return string.Concat(text.AsSpan(0, cut).TrimEnd(), "…");
    }
}
