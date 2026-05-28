namespace CodeSpace.Messages.Dtos.Chat;

/// <summary>
/// One <c>@</c>-reference on a message — the generic chip. <see cref="RefType"/> is an open
/// namespace (<c>user</c> / <c>pull_request</c> / <c>workflow</c> / <c>code_location</c> / …) and
/// <see cref="RefId"/> is interpreted per type by the frontend resolver; neither is hardcoded
/// server-side. <see cref="Label"/> is the cached display text captured when the message was
/// posted, so a chip renders without re-resolving the target.
/// </summary>
public sealed record MessageReferenceView
{
    public required string RefType { get; init; }
    public required string RefId { get; init; }
    public string? Label { get; init; }
}
