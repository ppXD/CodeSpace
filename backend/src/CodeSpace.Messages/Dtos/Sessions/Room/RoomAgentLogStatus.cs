using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Sessions.Room;

/// <summary>
/// One agent's durable log-stream health, reduced from its persisted streams. Lives in Messages because it is now a
/// WIRE value in its own right — <see cref="RoomArtifactProducer.Logs"/> carries it per artifact, alongside the
/// boolean <see cref="RoomArtifactVerification.LogsComplete"/> that already exposed its shape. The reduction itself
/// stays in the projector; only the vocabulary is shared.
///
/// <para>The converter rides on the TYPE, not on the API's global options, for the same reason
/// <see cref="RoomConfinementPosture"/> carries its own: the zero member would otherwise serialize as <c>0</c>
/// anywhere those options are absent (a cached payload, a test, a future non-MVC writer), and the frontend's
/// label lookup falls through on a falsy <c>0</c> — <see cref="Verified"/>, the healthiest fold, would render as
/// nothing at all.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
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
