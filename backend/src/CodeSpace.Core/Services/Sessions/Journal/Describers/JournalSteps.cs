using CodeSpace.Messages.Dtos.Sessions.Journal;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Sessions.Journal.Describers;

/// <summary>The shared event → step mapping every describer folds through — the common fields (id / time / copy / tone / prominence / provenance) carried straight off the merged timeline event; the describer supplies only the JOURNAL <see cref="JournalStep.Kind"/> classification. One place, so the mapping can't drift per describer.</summary>
internal static class JournalSteps
{
    public static JournalStep From(RunTimelineEvent e, string kind) => new()
    {
        Id = e.Id,
        At = e.OccurredAt,
        Kind = kind,
        Title = e.Title,
        Detail = e.Summary,
        Tone = e.Severity,
        Milestone = e.Level == TimelineLevel.Milestone,
        AgentRunId = e.AgentRunId,
        NodeId = e.NodeId,
    };
}
