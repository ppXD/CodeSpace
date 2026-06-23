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
            OccurredAt = e.OccurredAt,
            Order = e.Sequence,
            NodeId = nodeByAgent.TryGetValue(e.AgentRunId, out var node) ? node : null,
            AgentRunId = e.AgentRunId.ToString(),
            SourceKey = Key,
        };
    }

    private static TimelineSeverity SeverityFor(AgentEventKind kind) => kind switch
    {
        AgentEventKind.Error => TimelineSeverity.Error,
        AgentEventKind.Warning => TimelineSeverity.Warning,
        _ => TimelineSeverity.Info,
    };

    private static string Truncate(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max).TrimEnd(), "…");
}
