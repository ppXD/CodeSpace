using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Tasks;

/// <summary>A persisted routing decision. No session or run is created, and its reference grants no execution permission.</summary>
public sealed record TaskRoutePreviewResult
{
    public required RoutePlan Route { get; init; }
    public required string DeploymentAutonomyCeiling { get; init; }
    /// <summary>Absent on a legacy preview means no reusable decision was recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RouteSnapshotId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Database time at issue, so clients can schedule a refresh without assuming their wall clock matches the server.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedAt { get; init; }
}
