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
        var readiness = new StubReadiness();

        var result = await new AgentRunLogStorageResolver(route, readiness).ResolveAsync(teamId, CancellationToken.None);

        result.ShouldBe(new AgentRunLogStorageResolution.Ready(profileId, 11));
        route.Requests.ShouldBe([new StorageRouteSnapshotRequest(teamId, "agent-run-log/v1")]);
        readiness.TeamIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task Missing_route_is_bootstrapped_once_then_resolved_through_the_same_exact_route()
    {
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var route = new StubRouteResolver(
            new StorageRouteSnapshotResolution.Missing(),
            new StorageRouteSnapshotResolution.Ready(new StorageRouteSnapshot
            {
                RouteId = Guid.NewGuid(), RouteRevision = 1, DataClassTypeKey = AgentRunLogStorageResolver.DataClassTypeKey,
                StorageProfileId = profileId, StorageProfileRevision = 1, ProviderTypeKey = "local-rwx/v1",
                NamespaceFingerprint = $"sha256:{new string('b', 64)}",
            }));
        var readiness = new StubReadiness();

        var result = await new AgentRunLogStorageResolver(route, readiness).ResolveAsync(teamId, CancellationToken.None);

        result.ShouldBe(new AgentRunLogStorageResolution.Ready(profileId, 1));
        readiness.TeamIds.ShouldBe([teamId]);
        route.Requests.ShouldBe([
            new StorageRouteSnapshotRequest(teamId, AgentRunLogStorageResolver.DataClassTypeKey),
            new StorageRouteSnapshotRequest(teamId, AgentRunLogStorageResolver.DataClassTypeKey),
        ]);
    }

    [Fact]
    public async Task Non_missing_route_policy_failures_are_typed_and_never_bootstrap_or_fall_back()
    {
        // Deliberately asymmetric with the main artifact plane: a Draft route sends THAT plane to its local backend,
        // while Agent Run log capture has no local backend to fall back to, so an un-activated route stays a refusal.
        var cases = new (StorageRouteSnapshotResolution Resolution, AgentRunLogStorageProblemCode Expected)[]
        {
            (new StorageRouteSnapshotResolution.RouteNotActivated(), AgentRunLogStorageProblemCode.Inactive),
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
            var readiness = new StubReadiness();
            var result = await new AgentRunLogStorageResolver(route, readiness).ResolveAsync(Guid.NewGuid(), CancellationToken.None);
            result.ShouldBe(new AgentRunLogStorageResolution.Unavailable(expected));
            route.Requests.Count.ShouldBe(1);
            readiness.TeamIds.ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Missing_route_that_cannot_be_bootstrapped_remains_typed_missing_without_another_profile_fallback()
    {
        var route = new StubRouteResolver(new StorageRouteSnapshotResolution.Missing(), new StorageRouteSnapshotResolution.Missing());
        var readiness = new StubReadiness();

        var result = await new AgentRunLogStorageResolver(route, readiness).ResolveAsync(Guid.NewGuid(), CancellationToken.None);

        result.ShouldBe(new AgentRunLogStorageResolution.Unavailable(AgentRunLogStorageProblemCode.Missing));
        readiness.TeamIds.Count.ShouldBe(1);
        route.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_downgraded_into_capture_health()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var route = new StubRouteResolver(new StorageRouteSnapshotResolution.Cancelled());

        await Should.ThrowAsync<OperationCanceledException>(() => new AgentRunLogStorageResolver(route, new StubReadiness()).ResolveAsync(Guid.NewGuid(), cancellation.Token));
    }

    private sealed class StubRouteResolver(params StorageRouteSnapshotResolution[] results) : IStorageRouteSnapshotResolver
    {
        public List<StorageRouteSnapshotRequest> Requests { get; } = [];

        public Task<StorageRouteSnapshotResolution> ResolveAsync(StorageRouteSnapshotRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(results[Math.Min(Requests.Count - 1, results.Length - 1)]);
        }
    }

    private sealed class StubReadiness : IAgentRunLogStorageReadiness
    {
        public List<Guid> TeamIds { get; } = [];

        public Task EnsureDefaultRouteAsync(Guid teamId, CancellationToken cancellationToken)
        {
            TeamIds.Add(teamId);
            return Task.CompletedTask;
        }
    }
}
