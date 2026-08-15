using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

public interface IStorageRouteService
{
    Task<StoragePage<StorageRouteSummary>> ListPageAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken);
    Task<StorageRouteDetail?> GetAsync(Guid teamId, Guid routeId, string? revisionCursor, int revisionLimit, CancellationToken cancellationToken);
    Task<StorageRouteDetail> CreateAsync(Guid teamId, Guid actorId, CreateStorageRouteCommand command, CancellationToken cancellationToken);
    Task<StorageRouteDetail?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageRouteRevisionCommand command, CancellationToken cancellationToken);
    Task<StorageRouteDetail?> SetStateAsync(Guid teamId, Guid actorId, SetStorageRouteStateCommand command, CancellationToken cancellationToken);
}
