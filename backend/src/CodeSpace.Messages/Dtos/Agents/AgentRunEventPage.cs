namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>A stable ascending event window plus the two sequence edges needed for history scrollback and live tailing.</summary>
public sealed record AgentRunEventPage
{
    public required IReadOnlyList<AgentRunEventDto> Items { get; init; }
    public required bool HasOlder { get; init; }
    public required bool HasNewer { get; init; }

    /// <summary>Opaque invariant sequence to pass as the next Older cursor. Null means this snapshot has no older page.</summary>
    public string? NextOlderCursor { get; init; }

    /// <summary>Opaque invariant sequence to pass as the next Newer cursor. Empty Newer pages retain their input cursor; an empty initial tail starts at zero.</summary>
    public required string NextNewerCursor { get; init; }
}
