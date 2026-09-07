namespace CodeSpace.Core.Persistence.Entities;

/// <summary>A server-owned routing decision and its atomic run binding. It records no execution grant.</summary>
public sealed class TaskRouteSnapshot
{
    public Guid Id { get; set; }
    public Guid TeamId { get; set; }
    public Guid ActorUserId { get; set; }
    public string InputDigest { get; set; } = default!;
    public string SeedDigest { get; set; } = default!;
    public string PolicyFingerprint { get; set; } = default!;
    public string RouteJson { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public Guid? ConsumedRunId { get; set; }
    public string? ResultJson { get; set; }
}
