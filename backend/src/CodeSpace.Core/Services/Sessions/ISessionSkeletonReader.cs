using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Sessions;

/// <summary>
/// Internal hot-path session projection for Room and Journal. Unlike <see cref="ISessionReadService"/>, it carries
/// only the thread header and turn/attempt metadata those two surfaces consume; the public detail contract remains
/// on <see cref="ISessionReadService"/>.
/// </summary>
internal interface ISessionSkeletonReader : IScopedDependency
{
    Task<SessionSkeleton?> GetBySessionAsync(Guid sessionId, Guid teamId, CancellationToken cancellationToken);
    Task<SessionSkeleton?> GetByRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}

internal sealed record SessionSkeleton
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
    public required WorkSessionKind Kind { get; init; }
    public required WorkSessionStatus Status { get; init; }
    public int? AnchorTurnIndex { get; init; }
    public required IReadOnlyList<SessionTurn> Turns { get; init; }
}
