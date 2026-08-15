using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Credentials;

public interface IStorageCredentialService
{
    Task<IReadOnlyList<StorageCredentialMetadata>> ListAsync(Guid teamId, CancellationToken cancellationToken);
    Task<StorageCredentialMetadata?> GetAsync(Guid teamId, Guid credentialId, CancellationToken cancellationToken);
    Task<StorageCredentialMetadata> CreateAsync(Guid teamId, Guid actorId, CreateStorageCredentialCommand command, CancellationToken cancellationToken);
    Task<StorageCredentialMetadata?> AppendRevisionAsync(Guid teamId, Guid actorId, AppendStorageCredentialRevisionCommand command, CancellationToken cancellationToken);
    Task<StorageCredentialMetadata?> RevokeAsync(Guid teamId, Guid actorId, RevokeStorageCredentialCommand command, CancellationToken cancellationToken);
}
