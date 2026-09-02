namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>
/// One place this team's data is kept, as an operator thinks of it: an address, the key that reaches it, and what
/// lands in it.
///
/// <para>Three control-plane rows sit underneath - a credential, a profile and a route per data class - and this
/// projection deliberately does not expose them separately. They are one thing to create and one thing to repair;
/// nothing an operator decides is expressed by having three of them.</para>
/// </summary>
public sealed record StorageDestinationDetail
{
    /// <summary>The profile's id, which is the destination's identity: the credential and the routes hang off it.</summary>
    public required Guid ProfileId { get; init; }

    public required string Name { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required int ProfileRevision { get; init; }
    public required StorageProfileStateValue State { get; init; }

    /// <summary>Null for a provider with no secret inputs, which is the only case with no credential at all.</summary>
    public Guid? CredentialId { get; init; }

    public int? CredentialRevision { get; init; }

    /// <summary>The data classes whose NEXT write lands here. Bytes already stored elsewhere are unaffected.</summary>
    public required IReadOnlyList<string> DataClassTypeKeys { get; init; }
}
