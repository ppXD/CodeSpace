namespace CodeSpace.Messages.Agents;

/// <summary>
/// One model discovered by reflecting a provider's model endpoint (Rule 18.1 noun): the wire id + an optional label.
/// Distinct from the persisted <c>ModelCredentialModel</c> row: this is the transient discovered shape the refresh
/// UPSERTs onto a credential's list. The pool is capability-generic (structured output is the client's job), so a
/// reflected model carries no capability flag.
/// </summary>
public sealed record ReflectedModel
{
    public required string ModelId { get; init; }

    public string? DisplayName { get; init; }
}
