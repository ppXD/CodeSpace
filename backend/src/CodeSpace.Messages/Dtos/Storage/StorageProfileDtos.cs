using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Storage;

public sealed record StorageProfileSummary
{
    public required Guid Id { get; init; }
    public required string StableName { get; init; }
    public required StorageProfileStateValue State { get; init; }
    public required int CurrentRevision { get; init; }
    public required uint Xmin { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
}

public sealed record StorageProfileDetail
{
    public required Guid Id { get; init; }
    public required string StableName { get; init; }
    public required StorageProfileStateValue State { get; init; }
    public required int CurrentRevision { get; init; }
    public required uint Xmin { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required Guid CreatedBy { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
    public required Guid LastModifiedBy { get; init; }
    public required IReadOnlyList<StorageProfileRevisionDetail> Revisions { get; init; }
}

public sealed record StorageProfileRevisionDetail
{
    public required Guid Id { get; init; }
    public required int Revision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement NonSecretConfig { get; init; }
    public string? CredentialRef { get; init; }
    public required string NamespaceFingerprint { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required Guid CreatedBy { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageProfileStateValue
{
    Draft = 0,
    Active = 1,
    Disabled = 2,
    Retired = 3,
}
