namespace CodeSpace.Messages.Agents;

/// <summary>
/// What a tool-call row is stamped with when it parks for a human: the bearer the card resolves by, the deadline, and —
/// for a side-effecting call — the redacted preview the card shows and the target a rejection sticks to. Written in the
/// one park CAS, so a row awaiting approval always carries the preview (and pins) its card was built from.
/// </summary>
public sealed record ToolCallApprovalPark
{
    /// <summary>Server-side bearer the respond path matches on — never surfaced to a client.</summary>
    public required string Token { get; init; }

    public required DateTimeOffset DeadlineAt { get; init; }

    /// <summary>The serialized, already-redacted <see cref="ToolCallPreview"/>. Null for a decision, whose envelope is stashed on its own.</summary>
    public string? PreviewJson { get; init; }

    /// <summary>The server-derived key of the call's target (<see cref="ToolCallPreview.Target"/>). Null for a decision.</summary>
    public string? Target { get; init; }
}
