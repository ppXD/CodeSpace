using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Enumerates what a storage profile still holds, so an operator deciding its fate can see the artifacts rather than
/// a count of them.
///
/// <para>A sibling of <c>IPlacementIntegrityReader</c>, not a mode of it: that one answers a team-wide health
/// question and rides an index keyed on state, while this one is scoped to a profile and rides
/// <c>ux_artifact_location_profile_object_key</c>, whose leading column is the profile revision.</para>
/// </summary>
public interface IProfilePlacementReader : IScopedDependency
{
    Task<ProfilePlacementPage> ListAsync(Guid teamId, Guid profileId, string? cursor, int limit, CancellationToken cancellationToken);

    /// <summary>Per-state totals across every revision of the profile — the summary a refusal quotes and an operator budgets against.</summary>
    Task<IReadOnlyList<ProfilePlacementTotal>> TotalsAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken);
}
