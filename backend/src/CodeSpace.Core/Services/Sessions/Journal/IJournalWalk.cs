using CodeSpace.Messages.Dtos.Sessions.Journal;

namespace CodeSpace.Core.Services.Sessions.Journal;

/// <summary>
/// Walks a run's merged timeline into its CHRONOLOGICAL journal — the replay heart of the Session Journal. It reads the
/// ONE ordered event spine (<c>IRunTimelineProjector</c>: run-record + supervisor + agent-event + tool-call sources,
/// already merged by time), describes each event to a <see cref="JournalStep"/> via the generic
/// <see cref="IJournalStepDescriberRegistry"/> (an unknown event still becomes a step — never dropped), and assigns each
/// a monotonic <see cref="JournalStep.Seq"/> in that order — the streaming cursor a delta re-reads from. READ-ONLY.
/// </summary>
public interface IJournalWalk
{
    /// <summary>The run's journal steps, in execution order, each Seq-assigned. <c>null</c> when the run isn't the team's (404-conflate, mirroring the timeline projector — existence is never leaked); an EMPTY list when the run has no events yet.</summary>
    Task<IReadOnlyList<JournalStep>?> WalkAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}
