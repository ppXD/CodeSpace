using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Reports whether a storage profile can still name the artifact bytes this deployment wrote BEFORE the CAS plane.
///
/// <para>Those rows carry a <c>storage_url</c> and no <c>artifact_location</c>, so the verifier, the placement
/// readers, the abandonment drain and the retirement guard are all blind to them. This pass makes the tier countable
/// without making it writable: it resolves and asks, and writes nothing whatsoever.</para>
///
/// <para>A sibling of <c>IProfilePlacementReader</c> rather than a mode of it: that one reads placements a profile
/// holds, and the whole point of this population is that it holds none.</para>
/// </summary>
public interface ILegacyPlacementSurveyor : IScopedDependency
{
    Task<LegacyPlacementSurvey> SurveyAsync(Guid teamId, Guid profileId, int limit, CancellationToken cancellationToken);
}
