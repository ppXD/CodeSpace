using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Pure mapping from ONE <c>agent_run_event</c> harness log row to a narrative timeline event. Only the
/// NARRATIVE-worthy kinds (<see cref="Narrative"/>: reasoning, file edits, test output, errors, warnings, the final
/// summary) become events — the remaining chatter (assistant text, tool/command lines, queued/started/completed
/// lifecycle) stays in the per-agent timeline + the Trace tab, not the run story line. Reasoning IS surfaced (a folded
/// <see cref="TimelineLevel.Detail"/> beat) so the journal can show the chain-of-thought and Activity/journal converge on
/// one spine; it is emitted per thinking-BLOCK (bounded like file edits), not per token, so it never floods. Extracted
/// from the source so the kind set + the per-event shape are unit-testable without a database.
/// </summary>
public static class AgentEventTimelineMap
{
    /// <summary>The agent-events source's provenance key — stamped on every event this mapper emits.</summary>
    public const string Key = "agent-events";

    /// <summary>The timeline <see cref="RunTimelineEvent.Kind"/> a reasoning event carries (<c>agent.Reasoning</c>) — the one string the journal describer matches to classify a THINKING step, so the sub-classification can't drift from the emitted kind.</summary>
    public const string ReasoningKind = "agent." + nameof(AgentEventKind.Reasoning);

    /// <summary>The narrative-worthy harness event kinds — the ONLY kinds that surface on the run story line. Also the source's SQL filter (kind IN …), so a verbose run doesn't flood the timeline. Reasoning rides at <see cref="TimelineLevel.Detail"/> (folded), bounded per thinking-block.</summary>
    public static readonly AgentEventKind[] Narrative =
    {
        AgentEventKind.Reasoning,
        AgentEventKind.FileChanged,
        AgentEventKind.TestOutput,
        AgentEventKind.Error,
        AgentEventKind.Warning,
        AgentEventKind.FinalSummary,
    };

    /// <summary>The headline cap — a long FinalSummary (the whole report) is clamped here; the full text stays in the agent's own timeline + Trace.</summary>
    private const int MaxTitle = 200;

    public static RunTimelineEvent ToEvent(AgentRunEvent e, IReadOnlyDictionary<Guid, string?> nodeByAgent, IReadOnlyDictionary<Guid, AgentRunStatus> statusByAgent)
    {
        var headline = string.IsNullOrWhiteSpace(e.Text) ? HeadlineFor(e.Kind) : e.Text;

        return new RunTimelineEvent
        {
            Id = $"agent-{e.Id:N}",
            Kind = $"agent.{e.Kind}",
            Title = Truncate(headline, MaxTitle),
            Severity = SeverityFor(e, statusByAgent),
            Level = LevelFor(e.Kind),
            OccurredAt = e.OccurredAt,
            Order = e.Sequence,
            NodeId = nodeByAgent.TryGetValue(e.AgentRunId, out var node) ? node : null,
            AgentRunId = e.AgentRunId.ToString(),
            SourceKey = Key,
        };
    }

    /// <summary>A payload-less event's fallback headline — a human phrase per kind, never the raw enum token (a harness that emits an empty error/reasoning item must not surface a bare "Error"/"FinalSummary" as the story-line milestone). An unknown kind degrades to a run-neutral phrase, mirroring <c>SupervisorDecisionTimelineMap</c>'s open-verb default.</summary>
    private static string HeadlineFor(AgentEventKind kind) => kind switch
    {
        AgentEventKind.Error => "The agent hit an error",
        AgentEventKind.Warning => "The agent flagged a warning",
        AgentEventKind.FinalSummary => "The agent finished",
        AgentEventKind.TestOutput => "The agent ran tests",
        AgentEventKind.FileChanged => "The agent edited a file",
        AgentEventKind.Reasoning => "The agent reasoned",
        _ => "The agent reported an update",
    };

    // An agent's ERROR and its FINAL SUMMARY are story milestones (the failure + the conclusion the operator reads);
    // its file edits / test output / warnings are DETAIL the wave + the agent's own terminal already carry, so they
    // fold away on the run story line.
    private static TimelineLevel LevelFor(AgentEventKind kind) => kind switch
    {
        AgentEventKind.Error => TimelineLevel.Milestone,
        AgentEventKind.FinalSummary => TimelineLevel.Milestone,
        _ => TimelineLevel.Detail,
    };

    // Severity: an Error is always Error, a Warning always Warning. A FinalSummary — the CONCLUSION milestone the
    // operator reads — rides the agent's TERMINAL run status so a failed / timed-out / cancelled / needs-review agent's
    // closing beat doesn't read as a neutral success (it must not drift from its own agent card, which shows the same
    // status). The status is the run-neutral AgentRun ROW status, never a harness-specific payload, so the source stays
    // harness-neutral. TestOutput stays Info deliberately: its pass/fail detail lives in the event Text, and reading a
    // failure marker out of the harness-specific DataJson would couple this run-neutral source to one harness's shape —
    // a failing test run's overall verdict already lands on the agent's FinalSummary tone above.
    private static TimelineSeverity SeverityFor(AgentRunEvent e, IReadOnlyDictionary<Guid, AgentRunStatus> statusByAgent) => e.Kind switch
    {
        AgentEventKind.Error => TimelineSeverity.Error,
        AgentEventKind.Warning => TimelineSeverity.Warning,
        AgentEventKind.FinalSummary => FinalSummaryTone(statusByAgent.GetValueOrDefault(e.AgentRunId, AgentRunStatus.Running)),
        _ => TimelineSeverity.Info,
    };

    /// <summary>The FinalSummary tone off the agent's TERMINAL status: a clean success is Success, a failed / timed-out / cancelled agent is Error, a needs-review agent is Warning, and an in-flight / unknown status is neutral Info.</summary>
    private static TimelineSeverity FinalSummaryTone(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Succeeded => TimelineSeverity.Success,
        AgentRunStatus.Failed or AgentRunStatus.TimedOut or AgentRunStatus.Cancelled => TimelineSeverity.Error,
        AgentRunStatus.NeedsReview => TimelineSeverity.Warning,
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
