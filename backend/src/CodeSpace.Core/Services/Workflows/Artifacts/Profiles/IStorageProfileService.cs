using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Profiles;

public interface IStorageProfileService
{
    Task<IReadOnlyList<StorageProfileSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken);
    Task<StorageProfileDetail?> GetAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken);
    Task<StorageProfileDetail> CreateAsync(Guid teamId, Guid actorId, CreateStorageProfileCommand command, CancellationToken cancellationToken);
    Task<StorageProfileDetail?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageProfileRevisionCommand command, CancellationToken cancellationToken);
    Task<StorageProfileDetail?> SetStateAsync(Guid teamId, Guid actorId, SetStorageProfileStateCommand command, CancellationToken cancellationToken);
}
