using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>Safe current metadata for a team-owned storage credential; secret material and ciphertext are absent.</summary>
public sealed record StorageCredentialMetadata
{
    public required Guid Id { get; init; }
    public required string StableName { get; init; }
    public required StorageCredentialStateValue State { get; init; }
    public required int CurrentRevision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public string? SafeHint { get; init; }
    public required string CredentialRef { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset CurrentRevisionCreatedDate { get; init; }
    public DateTimeOffset? RevokedDate { get; init; }
    public required uint Xmin { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageCredentialStateValue
{
    Active = 0,
    Revoked = 1,
}
