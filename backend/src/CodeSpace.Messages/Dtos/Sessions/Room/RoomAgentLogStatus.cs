namespace CodeSpace.Messages.Dtos.Sessions.Room;

/// <summary>
/// One agent's durable log-stream health, reduced from its persisted streams. Lives in Messages because it is now a
/// WIRE value in its own right — <see cref="RoomArtifactProducer.Logs"/> carries it per artifact, alongside the
/// boolean <see cref="RoomArtifactVerification.LogsComplete"/> that already exposed its shape. The reduction itself
/// stays in the projector; only the vocabulary is shared.
/// </summary>
public enum RoomAgentLogStatus
{
    Verified,
    Captured,
    Finalizing,
    Incomplete,

    /// <summary>
    /// Open, and its remote storage is refusing the segments it is holding. Distinct from <see cref="Finalizing"/>
    /// because nothing is progressing and nothing is lost either — the bytes are queued behind an outage, which is the
    /// one fact an operator can act on. Appended rather than slotted into the severity order (which
    /// <c>RoomNarrative.LogStatusRank</c> owns explicitly) so no existing member's ordinal moves: nothing persists one
    /// today, and a value shifted underneath a stored ordinal is unrecoverable.
    /// </summary>
    Stalled,
}
