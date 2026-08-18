using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Routing;

/// <summary>
/// The write-time destination decision for the MAIN artifact plane. Two properties matter more than the mapping table:
/// an unrouted team keeps local disk (so every existing deployment is untouched), and a route that exists but cannot
/// take bytes never degrades into local disk — that silent fallback is the dishonesty this plane exists to remove.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WorkflowArtifactDestinationResolverTests
{
    [Fact]
    public void The_versioned_data_class_key_is_pinned()
    {
        // The key is the operator's Settings row and every artifact_location stamped through it. A rename orphans
        // every configured route and silently returns those teams to local disk.
        WorkflowArtifactDestinationResolver.DataClassTypeKey.ShouldBe("workflow-artifact/v1");
    }

    [Fact]
    public async Task No_route_at_all_keeps_the_local_backend_and_never_bootstraps_one()
    {
        var routes = new StubRouteResolver(new StorageRouteSnapshotResolution.Missing());

        var destination = await new WorkflowArtifactDestinationResolver(routes).ResolveAsync(Guid.NewGuid(), CancellationToken.None);

        destination.ShouldBeOfType<WorkflowArtifactDestination.Local>();
        routes.Requests.Count.ShouldBe(1, "a missing route is the shipped default, not something to repair on the write path");
    }

    [Fact]
    public async Task Ready_route_forwards_the_exact_frozen_profile_coordinates()
    {
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var routes = new StubRouteResolver(new StorageRouteSnapshotResolution.Ready(new StorageRouteSnapshot
        {
            RouteId = Guid.NewGuid(), RouteRevision = 4, DataClassTypeKey = WorkflowArtifactDestinationResolver.DataClassTypeKey,
            StorageProfileId = profileId, StorageProfileRevision = 9, ProviderTypeKey = "local-rwx/v1",
            NamespaceFingerprint = $"sha256:{new string('a', 64)}",
        }));

        var destination = await new WorkflowArtifactDestinationResolver(routes).ResolveAsync(teamId, CancellationToken.None);

        destination.ShouldBe(new WorkflowArtifactDestination.Routed(profileId, 9));
        routes.Requests.ShouldBe([new StorageRouteSnapshotRequest(teamId, "workflow-artifact/v1")]);
    }

    [Theory]
    [MemberData(nameof(UnusableResolutions))]
    public async Task A_route_that_cannot_take_bytes_fails_closed_instead_of_falling_back_to_local(StorageRouteSnapshotResolution resolution, WorkflowArtifactDestinationProblem expected)
    {
        var routes = new StubRouteResolver(resolution);

        var destination = await new WorkflowArtifactDestinationResolver(routes).ResolveAsync(Guid.NewGuid(), CancellationToken.None);

        destination.ShouldBe(new WorkflowArtifactDestination.Unusable(expected));
    }

    public static TheoryData<StorageRouteSnapshotResolution, WorkflowArtifactDestinationProblem> UnusableResolutions() => new()
    {
        { new StorageRouteSnapshotResolution.RouteNotActive(), WorkflowArtifactDestinationProblem.RouteNotActive },
        { new StorageRouteSnapshotResolution.ProfileNotActive(), WorkflowArtifactDestinationProblem.ProfileNotActive },
        { new StorageRouteSnapshotResolution.RouteRevisionMissing(), WorkflowArtifactDestinationProblem.Invalid },
        { new StorageRouteSnapshotResolution.ProfileMissing(), WorkflowArtifactDestinationProblem.Invalid },
        { new StorageRouteSnapshotResolution.ProfileRevisionMissing(), WorkflowArtifactDestinationProblem.Invalid },
        { new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProfileRevisionMode), WorkflowArtifactDestinationProblem.Invalid },
        { new StorageRouteSnapshotResolution.Cancelled(), WorkflowArtifactDestinationProblem.ResolutionFailed },
    };

    [Fact]
    public async Task Caller_cancellation_is_not_downgraded_into_a_storage_policy_verdict()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var routes = new StubRouteResolver(new StorageRouteSnapshotResolution.Cancelled());

        await Should.ThrowAsync<OperationCanceledException>(
            () => new WorkflowArtifactDestinationResolver(routes).ResolveAsync(Guid.NewGuid(), cancellation.Token));
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
}
