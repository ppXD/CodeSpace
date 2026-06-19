namespace CodeSpace.Messages.Agents;

/// <summary>
/// One model discovered by reflecting a provider's model endpoint (Rule 18.1 noun): the wire id, an optional label,
/// and whether it supports structured output — enriched from the in-code <c>BuiltinModelCatalog</c> when the id is
/// known, else the safe false floor. Distinct from the persisted <c>ModelCredentialModel</c> row: this is the
/// transient discovered shape the refresh UPSERTs onto a credential's list.
/// </summary>
public sealed record ReflectedModel
{
    public required string ModelId { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>Whether the model can return structured / JSON-schema output — the one capability the scheduler gates on.</summary>
    public bool SupportsStructuredOutput { get; init; }
}
