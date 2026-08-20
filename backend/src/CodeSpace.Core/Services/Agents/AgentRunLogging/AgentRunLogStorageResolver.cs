using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing.DataClasses;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// Translates the shared routed destination into the Agent Run log capture vocabulary, and owns the one step that is
/// this class's own: a team that has never configured log storage gets its explicit default route established once,
/// then the same exact route is resolved again. The returned coordinates are frozen for the write and no profile-name
/// or single-candidate fallback is permitted.
///
/// <para>Deliberately asymmetric with the main artifact plane, which sends an un-activated (Draft) route to its local
/// blob backend: this data class HAS no local backend, so there is nothing to degrade to and an un-activated route
/// stays a typed refusal rather than becoming silently-dropped capture. That is declared by
/// <see cref="AgentRunLogDataClass"/> NOT implementing <see cref="IRoutedDataClassLocalFallback"/> — the absence is
/// the policy, and <c>RoutedDestination.Local</c> is consequently unreachable here.</para>
/// </summary>
public sealed class AgentRunLogStorageResolver : IAgentRunLogStorageResolver
{
    public const string DataClassTypeKey = "agent-run-log/v1";
    private readonly IRoutedDestinationResolver _destinations;
    private readonly IAgentRunLogStorageReadiness _readiness;
    private readonly AgentRunLogDataClass _dataClass;

    public AgentRunLogStorageResolver(IRoutedDestinationResolver destinations, IAgentRunLogStorageReadiness readiness, AgentRunLogDataClass dataClass)
    {
        _destinations = destinations;
        _readiness = readiness;
        _dataClass = dataClass;
    }

    public async Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var destination = await _destinations.ResolveAsync(_dataClass, teamId, cancellationToken).ConfigureAwait(false);
        if (destination is RoutedDestination.Unusable { Disposition: RoutedDestinationDisposition.NoRoute })
        {
            await _readiness.EnsureDefaultRouteAsync(teamId, cancellationToken).ConfigureAwait(false);
            destination = await _destinations.ResolveAsync(_dataClass, teamId, cancellationToken).ConfigureAwait(false);
        }

        return destination switch
        {
            RoutedDestination.Routed routed => new AgentRunLogStorageResolution.Ready(routed.StorageProfileId, routed.StorageProfileRevision),
            RoutedDestination.Unusable unusable => new AgentRunLogStorageResolution.Unavailable(Problem(unusable.Disposition)),
            _ => throw new InvalidOperationException($"Data class '{_dataClass.TypeKey}' resolved to a local destination it has no backend for. Only a class with somewhere to put the bytes may declare {nameof(IRoutedDataClassLocalFallback)}."),
        };
    }

    /// <summary>
    /// Total over the shared vocabulary. This plane's codes are coarser on purpose — every "a route or profile is not
    /// taking bytes" state is one operator-facing <c>Inactive</c> — while the shared disposition keeps them distinct
    /// for consumers that report them separately.
    /// </summary>
    private static AgentRunLogStorageProblemCode Problem(RoutedDestinationDisposition disposition) => disposition switch
    {
        RoutedDestinationDisposition.NoRoute => AgentRunLogStorageProblemCode.Missing,
        RoutedDestinationDisposition.RouteNotActivated or RoutedDestinationDisposition.RouteNotActive
            or RoutedDestinationDisposition.ProfileNotActive => AgentRunLogStorageProblemCode.Inactive,
        RoutedDestinationDisposition.Invalid => AgentRunLogStorageProblemCode.Invalid,
        _ => AgentRunLogStorageProblemCode.ResolutionFailed,
    };
}
