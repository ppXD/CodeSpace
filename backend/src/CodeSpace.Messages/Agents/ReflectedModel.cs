namespace CodeSpace.Messages.Agents;

/// <summary>
/// One model discovered by reflecting a provider's model endpoint (Rule 18.1 noun): the wire id, an optional label,
/// and the capability boundary — enriched from the in-code <c>BuiltinModelCatalog</c> when the id is known, else the
/// all-false floor. Distinct from the persisted <c>ModelCredentialModel</c> row: this is the transient discovered
/// shape the refresh UPSERTs onto a credential's list.
/// </summary>
public sealed record ReflectedModel
{
    public required string ModelId { get; init; }

    public string? DisplayName { get; init; }

    public ModelCapabilityFlags Capabilities { get; init; } = new();
}
