namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// A pending callback wait's URL, handed out by <c>RevealWorkflowCallbackUrlCommand</c>. <see cref="Path"/> is the path
/// on this API (<c>/api/workflows/callbacks/{token}</c>); the caller puts the origin it reaches the API on in front of it,
/// as the run page always did. Anyone holding it can resume the run without signing in.
/// </summary>
public sealed record WorkflowCallbackUrl
{
    public required Guid WaitId { get; init; }

    public required string Path { get; init; }
}
