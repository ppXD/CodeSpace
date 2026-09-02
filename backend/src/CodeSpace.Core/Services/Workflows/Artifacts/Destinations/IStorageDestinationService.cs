using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Destinations;

/// <summary>
/// Assembles a destination out of the three control-plane concerns that make one up, and is the only place that
/// knows they are one thing.
///
/// <para>A peer of the credential, profile and route services rather than a layer over them (Rule 18.2): it composes
/// WITH them and owns no rows of its own. Every rule stays where it was decided - secret admission in the credential
/// service, configuration admission and lifecycle in the profile service, the destination write probe in the route
/// service - so this cannot become a second, divergent set of rules for creating the same rows.</para>
///
/// <para>What it adds is atomicity. The underlying steps are individually irreversible: neither a credential nor a
/// profile can be deleted, because a stored object's location stamps the exact revision it was written under. A
/// caller that ran them in sequence and lost one in the middle would leave a half-built destination nobody can
/// remove.</para>
/// </summary>
public interface IStorageDestinationService : CodeSpace.Core.DependencyInjection.IScopedDependency
{
    Task<StorageDestinationDetail> CreateAsync(Guid teamId, Guid actorId, CreateStorageDestinationCommand command, CancellationToken cancellationToken);
}
