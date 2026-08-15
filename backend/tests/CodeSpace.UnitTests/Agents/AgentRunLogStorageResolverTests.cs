using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

public sealed class AgentRunLogStorageResolverTests
{
    [Fact]
    public async Task Ready_route_forwards_the_exact_frozen_profile_coordinates_for_the_versioned_log_data_class()
    {
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var snapshot = new StorageRouteSnapshot
        {
            RouteId = Guid.NewGuid(), RouteRevision = 7, DataClassTypeKey = AgentRunLogStorageResolver.DataClassTypeKey,
            StorageProfileId = profileId, StorageProfileRevision = 11, ProviderTypeKey = "local-rwx/v1",
            NamespaceFingerprint = $"sha256:{new string('a', 64)}",
        };
        var route = new StubRouteResolver(new StorageRouteSnapshotResolution.Ready(snapshot));

        var result = await new AgentRunLogStorageResolver(route).ResolveAsync(teamId, CancellationToken.None);

        result.ShouldBe(new AgentRunLogStorageResolution.Ready(profileId, 11));
        route.Requests.ShouldBe([new StorageRouteSnapshotRequest(teamId, "agent-run-log/v1")]);
    }

    [Fact]
    public async Task Route_policy_failures_are_typed_and_never_fall_back_to_an_unrelated_profile()
    {
        var cases = new (StorageRouteSnapshotResolution Resolution, AgentRunLogStorageProblemCode Expected)[]
        {
            (new StorageRouteSnapshotResolution.Missing(), AgentRunLogStorageProblemCode.Missing),
            (new StorageRouteSnapshotResolution.RouteNotActive(), AgentRunLogStorageProblemCode.Inactive),
            (new StorageRouteSnapshotResolution.ProfileNotActive(), AgentRunLogStorageProblemCode.Inactive),
            (new StorageRouteSnapshotResolution.RouteRevisionMissing(), AgentRunLogStorageProblemCode.Invalid),
            (new StorageRouteSnapshotResolution.ProfileMissing(), AgentRunLogStorageProblemCode.Invalid),
            (new StorageRouteSnapshotResolution.ProfileRevisionMissing(), AgentRunLogStorageProblemCode.Invalid),
            (new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProfileRevisionMode), AgentRunLogStorageProblemCode.Invalid),
            (new StorageRouteSnapshotResolution.Cancelled(), AgentRunLogStorageProblemCode.ResolutionFailed),
        };

        foreach (var (resolution, expected) in cases)
        {
            var route = new StubRouteResolver(resolution);
            var result = await new AgentRunLogStorageResolver(route).ResolveAsync(Guid.NewGuid(), CancellationToken.None);
            result.ShouldBe(new AgentRunLogStorageResolution.Unavailable(expected));
            route.Requests.Count.ShouldBe(1);
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_not_downgraded_into_capture_health()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var route = new StubRouteResolver(new StorageRouteSnapshotResolution.Cancelled());

        await Should.ThrowAsync<OperationCanceledException>(() => new AgentRunLogStorageResolver(route).ResolveAsync(Guid.NewGuid(), cancellation.Token));
    }

    private sealed class StubRouteResolver(StorageRouteSnapshotResolution result) : IStorageRouteSnapshotResolver
    {
        public List<StorageRouteSnapshotRequest> Requests { get; } = [];

        public Task<StorageRouteSnapshotResolution> ResolveAsync(StorageRouteSnapshotRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
