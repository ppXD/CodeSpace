using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Storage;

public static class StorageRouteRevisionPageLimits
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
}

public sealed record StorageRouteSummary
{
    public required Guid Id { get; init; }
    public required string DataClassTypeKey { get; init; }
    public required StorageRouteStateValue State { get; init; }
    public required int CurrentRevision { get; init; }
    public required uint Xmin { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required string StorageProfileStableName { get; init; }
    public required StorageProfileRevisionModeValue ProfileRevisionMode { get; init; }
    public int? PinnedProfileRevision { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
}

public sealed record StorageRouteDetail
{
    public required Guid Id { get; init; }
    public required string DataClassTypeKey { get; init; }
    public required StorageRouteStateValue State { get; init; }
    public required int CurrentRevision { get; init; }
    public required uint Xmin { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required Guid CreatedBy { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
    public required Guid LastModifiedBy { get; init; }
    public required StorageRouteRevisionDetail CurrentTarget { get; init; }
    public required StoragePage<StorageRouteRevisionDetail> RevisionPage { get; init; }
}

public sealed record StorageRouteRevisionDetail
{
    public required Guid Id { get; init; }
    public required int Revision { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required string StorageProfileStableName { get; init; }
    public required StorageProfileRevisionModeValue ProfileRevisionMode { get; init; }
    public int? PinnedProfileRevision { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required Guid CreatedBy { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageRouteStateValue
{
    Draft = 0,
    Active = 1,
    Disabled = 2,
    Retired = 3,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageProfileRevisionModeValue
{
    CurrentAtWrite = 0,
    Pinned = 1,
}
