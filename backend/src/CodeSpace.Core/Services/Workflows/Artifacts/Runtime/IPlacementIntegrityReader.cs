using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Reports whether the bytes a team has already written are still at their destinations.
///
/// <para>A sibling of the profile and health services rather than a method on either: health describes a destination
/// that can be probed now, and a profile describes where writes go next. Neither can answer whether an object written
/// a year ago is still there, and widening them to try would put a question about placements on a type that owns
/// none.</para>
/// </summary>
public interface IPlacementIntegrityReader : IScopedDependency
{
    Task<PlacementIntegritySummary> ReadAsync(Guid teamId, CancellationToken cancellationToken);
}
