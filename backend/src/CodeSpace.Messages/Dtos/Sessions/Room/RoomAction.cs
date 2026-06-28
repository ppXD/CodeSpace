using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Sessions.Room;

/// <summary>
/// A capability-aware action the user can take on a turn. <see cref="Enabled"/> + <see cref="DisabledReason"/> are
/// computed by the backend from the SAME gates the write path enforces, so a click never 422s — a disabled action shows
/// its reason instead of failing. Rerun / replay set <see cref="Attempt"/> (they fork an attempt of this turn, not a new turn).
/// </summary>
public sealed record RoomAction
{
    public required RoomActionKind Kind { get; init; }
    public required string Label { get; init; }
    public required bool Enabled { get; init; }

    /// <summary>Why the action is unavailable right now — shown in place of failing. Null when <see cref="Enabled"/>.</summary>
    public string? DisabledReason { get; init; }

    /// <summary>What the action operates on — a node id (<see cref="RoomActionKind.RerunFromNode"/>), the run id (open trace), etc. Null when not needed.</summary>
    public string? Target { get; init; }

    /// <summary>True when this action forks a new ATTEMPT of the turn (rerun / replay) rather than starting a fresh turn.</summary>
    public bool Attempt { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RoomActionKind
{
    RerunTurn,
    RerunFromNode,
    RerunFailedMapItems,
    RetryFailedAgent,
    AnswerDecision,
    Stop,
    OpenTrace,
}
