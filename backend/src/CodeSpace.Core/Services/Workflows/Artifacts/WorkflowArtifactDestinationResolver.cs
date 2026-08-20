using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing.DataClasses;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// Translates the shared routed destination into the main artifact plane's own vocabulary. The POLICY lives in
/// <see cref="IRoutedDestinationResolver"/>; what is left here is a total mapping between two closed vocabularies plus
/// the versioned key this plane reads.
///
/// <para>Unlike the Agent Run log class this one has NO bootstrap: a team with no route keeps the local backend
/// verbatim, so adopting routing is an explicit operator act rather than something a first write invents. That
/// difference is now declared, not written twice — <see cref="WorkflowArtifactDataClass"/> implements
/// <see cref="IRoutedDataClassLocalFallback"/>, which is what turns "no route" and "route never activated" into
/// <c>RoutedDestination.Local</c> here. Remove the declaration and both would fail closed instead; they would never
/// reach local disk by accident.</para>
/// </summary>
public sealed class WorkflowArtifactDestinationResolver : IWorkflowArtifactDestinationResolver
{
    public const string DataClassTypeKey = "workflow-artifact/v1";
    private readonly IRoutedDestinationResolver _destinations;
    private readonly WorkflowArtifactDataClass _dataClass;

    public WorkflowArtifactDestinationResolver(IRoutedDestinationResolver destinations, WorkflowArtifactDataClass dataClass)
    {
        _destinations = destinations;
        _dataClass = dataClass;
    }

    public async Task<WorkflowArtifactDestination> ResolveAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var destination = await _destinations.ResolveAsync(_dataClass, teamId, cancellationToken).ConfigureAwait(false);

        return destination switch
        {
            RoutedDestination.Routed routed => new WorkflowArtifactDestination.Routed(routed.StorageProfileId, routed.StorageProfileRevision),
            RoutedDestination.Local => new WorkflowArtifactDestination.Local(),
            RoutedDestination.Unusable unusable => new WorkflowArtifactDestination.Unusable(Problem(unusable.Disposition)),
            _ => new WorkflowArtifactDestination.Unusable(WorkflowArtifactDestinationProblem.ResolutionFailed),
        };
    }

    /// <summary>
    /// Total over the shared vocabulary. The two pre-cutover dispositions arrive as <c>Local</c> for this class, so
    /// they are not enumerated here; were the local-home declaration dropped they would fall through to
    /// <see cref="WorkflowArtifactDestinationProblem.ResolutionFailed"/> — a refusal, never a silent local write.
    /// </summary>
    private static WorkflowArtifactDestinationProblem Problem(RoutedDestinationDisposition disposition) => disposition switch
    {
        RoutedDestinationDisposition.RouteNotActive => WorkflowArtifactDestinationProblem.RouteNotActive,
        RoutedDestinationDisposition.ProfileNotActive => WorkflowArtifactDestinationProblem.ProfileNotActive,
        RoutedDestinationDisposition.Invalid => WorkflowArtifactDestinationProblem.Invalid,
        _ => WorkflowArtifactDestinationProblem.ResolutionFailed,
    };
}
