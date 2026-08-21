namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>A hard-bounded chronological metadata window over one Agent Run's governed tool-call ledger.</summary>
public sealed record ToolCallPage
{
    public required Guid AgentRunId { get; init; }
    public required string Mode { get; init; }
    public string? RequestCursor { get; init; }
    public required IReadOnlyList<ToolCallView> Items { get; init; }
    public required bool HasOlder { get; init; }
    public string? NextOlderCursor { get; init; }
}
