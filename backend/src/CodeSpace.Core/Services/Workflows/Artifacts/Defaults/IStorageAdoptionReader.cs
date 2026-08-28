using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Defaults;

/// <summary>
/// The READ side of adoption: for one team, every routed data class and where it stands on the deployment's default.
///
/// <para>A sibling of <see cref="IStorageDefaultMaterializer"/> rather than a method on it (Rule 7). The materializer
/// takes a team irreversibly off its current storage; this answers a question. Nothing that only wants the answer
/// should have to hold a reference to the thing that can perform the act.</para>
/// </summary>
public interface IStorageAdoptionReader
{
    Task<IReadOnlyList<StorageAdoptionStatus>> ReadAsync(Guid teamId, CancellationToken cancellationToken);
}
