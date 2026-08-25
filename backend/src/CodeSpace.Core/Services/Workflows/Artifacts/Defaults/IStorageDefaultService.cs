using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

/// <summary>
/// Deployment-admin control plane for the storage template — one operator-authored default per routed data class,
/// plus the instance-scope ciphertext it needs.
///
/// <para><b>Nothing in this build reads what it writes.</b> No team resolves storage through a template, no route is
/// created from one, and no byte moves because one exists. The intended reader is the materializer lane. Every method
/// here is deliberately team-less: a template describes the whole deployment, so passing a team would be meaningless
/// and — via the SPA's ambient <c>X-Team-Id</c> — actively dangerous.</para>
/// </summary>
public interface IStorageDefaultService
{
    Task<IReadOnlyList<StorageDefaultSummary>> ListAsync(CancellationToken cancellationToken);
    Task<StorageDefaultDetail?> GetAsync(Guid defaultId, CancellationToken cancellationToken);
    Task<StorageDefaultDetail> CreateAsync(Guid actorId, CreateStorageDefaultCommand command, CancellationToken cancellationToken);
    Task<StorageDefaultDetail?> UpdateAsync(Guid actorId, UpdateStorageDefaultCommand command, CancellationToken cancellationToken);
    Task<StorageDefaultDetail?> SetEnabledAsync(Guid actorId, SetStorageDefaultEnabledCommand command, CancellationToken cancellationToken);
}
