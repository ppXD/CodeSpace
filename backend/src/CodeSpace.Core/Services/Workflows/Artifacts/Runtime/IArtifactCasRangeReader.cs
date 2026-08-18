using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Reads ONE bounded window of a stored artifact object by asking the provider for that window, so a paging viewer
/// costs the bytes it displays rather than the bytes that precede them.
///
/// <para>A SIBLING of <see cref="IArtifactCasRuntimeCoordinator"/> rather than a widening of it: the whole-object read
/// verifies the complete SHA-256 at EOF and a window cannot, so the two carry genuinely different guarantees and must
/// not be reachable through one contract. The caller of a window read owns its own integrity policy exactly as it does
/// for the local blob backend's ranged read.</para>
/// </summary>
public interface IArtifactCasRangeReader : IScopedDependency
{
    Task<ArtifactCasRangeResult> ReadRangeAsync(ArtifactCasRangeRequest request, CancellationToken cancellationToken);
}

public sealed record ArtifactCasRangeRequest
{
    public required Guid TeamId { get; init; }
    public required Guid ArtifactObjectId { get; init; }
    public required Guid StorageProfileId { get; init; }
    public required int StorageProfileRevision { get; init; }

    /// <summary>First byte of the window, counted from the start of the object.</summary>
    public required long Offset { get; init; }

    /// <summary>Maximum bytes to return. The window is clamped to what remains after <see cref="Offset"/>.</summary>
    public required int Length { get; init; }

    public TimeSpan? OperationTimeout { get; init; }
}

public abstract record ArtifactCasRangeResult
{
    private ArtifactCasRangeResult() { }

    /// <summary>
    /// The requested window, plus the object's full length from the durable ledger. The bytes are NOT digest-verified
    /// — a window cannot be, since the recorded digest covers the whole object.
    /// </summary>
    public sealed record Available(byte[] Bytes, long TotalLength) : ArtifactCasRangeResult;

    public sealed record Unavailable(ArtifactCasProblem Problem) : ArtifactCasRangeResult;
}
