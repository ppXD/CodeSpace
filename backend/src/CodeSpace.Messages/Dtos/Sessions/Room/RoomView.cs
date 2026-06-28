using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Sessions.Room;

/// <summary>
/// The backend-authored AI WORK TRANSCRIPT for a session — a flat, render-ready list of <see cref="RoomBlock"/>s the
/// frontend renders purely by <c>type</c> (it never inspects run / node / status to decide copy or order; the backend
/// owns all of that). Streaming-ready: every block carries a stable <see cref="RoomBlock.Id"/> + a monotonic
/// <see cref="RoomBlock.Seq"/>, so a delta (<c>?after={seq}</c>) or an SSE stream can add / update blocks by id without
/// resending the whole transcript. Run / phase / agent / decision are the MATERIALS the projector reads — never the
/// language shown.
/// </summary>
public sealed record RoomView
{
    public required Guid SessionId { get; init; }
    public required string Title { get; init; }
    public required WorkSessionKind Kind { get; init; }
    public required WorkSessionStatus Status { get; init; }

    /// <summary>The high-water <see cref="RoomBlock.Seq"/> across all blocks — the client echoes it back as <c>?after=</c> for the next delta.</summary>
    public required long Cursor { get; init; }

    /// <summary>When entered by a run id, the block id to scroll to (the turn that run belongs to). Null when entered by session id.</summary>
    public string? AnchorBlockId { get; init; }

    public required IReadOnlyList<RoomBlock> Blocks { get; init; }
}
