using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Routing;

public interface IStorageRouteService
{
    Task<StoragePage<StorageRouteSummary>> ListPageAsync(Guid teamId, string? cursor, int limit, CancellationToken cancellationToken);
    Task<StorageRouteDetail?> GetAsync(Guid teamId, Guid routeId, string? revisionCursor, int revisionLimit, CancellationToken cancellationToken);

    /// <summary>
    /// The team's one route for a data class, whatever state it is in, or null when nobody has ever routed that
    /// class. A data class carries exactly one route row for the life of the team - <c>CreateAsync</c> refuses a
    /// second - so this is the lookup a caller needs to tell "route it" from "repoint it".
    /// </summary>
    Task<StorageRouteSummary?> GetByDataClassAsync(Guid teamId, string dataClassTypeKey, CancellationToken cancellationToken);
    Task<StorageRouteDetail> CreateAsync(Guid teamId, Guid actorId, CreateStorageRouteCommand command, CancellationToken cancellationToken);
    Task<StorageRouteDetail?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageRouteRevisionCommand command, CancellationToken cancellationToken);
    Task<StorageRouteDetail?> SetStateAsync(Guid teamId, Guid actorId, SetStorageRouteStateCommand command, CancellationToken cancellationToken);
}
