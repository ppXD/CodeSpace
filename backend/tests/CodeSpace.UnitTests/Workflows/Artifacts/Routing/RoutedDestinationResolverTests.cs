using CodeSpace.Core.Services.Workflows.Artifacts.Routing;
using CodeSpace.Core.Services.Workflows.Artifacts.Routing.DataClasses;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Routing;

/// <summary>
/// The write-time destination decision, exercised through data classes that exist ONLY in this file. Neither has a
/// resolver, a destination union or a problem enum of its own — a declaration is the whole of it. That is the
/// enforceable form of the claim that adding a routed data class is a declaration rather than a second hand-written
/// copy of the same policy, and it is what the two shipped classes could not demonstrate while each owned its switch.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RoutedDestinationResolverTests
{
    private const string SyntheticKey = "synthetic-probe/v1";

    [Fact]
    public async Task A_declaration_alone_resolves_the_exact_frozen_profile_coordinates()
    {
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var routes = new StubRouteResolver(new StorageRouteSnapshotResolution.Ready(Snapshot(profileId, 9)));

        var destination = await new RoutedDestinationResolver(routes).ResolveAsync(new SyntheticDataClass(), teamId, CancellationToken.None);

        destination.ShouldBe(new RoutedDestination.Routed(profileId, 9));
        routes.Requests.ShouldBe([new StorageRouteSnapshotRequest(teamId, SyntheticKey)], "the declaration's own key is what the routing plane is asked for");
    }

    /// <summary>
    /// The whole policy table, and the ONE axis the two shipped classes deliberately differ on: whether a class that
    /// was never cut over has a lawful home outside the routing plane. Both declarations are asserted per row, so a
    /// change that quietly sends a refusing class to a local home — or stops an accepting one from reaching it — fails
    /// here rather than in one of the two consumers.
    /// </summary>
    [Theory]
    [MemberData(nameof(NonReadyResolutions))]
    public async Task The_local_home_declaration_is_the_only_axis_a_declaration_can_change(StorageRouteSnapshotResolution resolution, RoutedDestinationDisposition expected, bool localHomeApplies)
    {
        var refusing = await ResolveAsync(new SyntheticDataClass(), resolution);
        var accepting = await ResolveAsync(new SyntheticDataClassWithLocalHome(), resolution);

        refusing.ShouldBe(new RoutedDestination.Unusable(expected), "a class that declared no local home must never be sent to one");
        accepting.ShouldBe(localHomeApplies
            ? new RoutedDestination.Local(expected)
            : new RoutedDestination.Unusable(expected), "only the two pre-cutover outcomes are lawfully local; a stopped route is a refusal for every class");
    }

    /// <summary>
    /// No route at all is the shipped state of every team that never configured one. A Draft route is the state every
    /// route is born in and — <c>StorageRouteRules.EnsureTransition</c> forbidding every transition back to it — the
    /// only state that provably means "not cut over yet" rather than "an operator stopped this".
    /// </summary>
    public static TheoryData<StorageRouteSnapshotResolution, RoutedDestinationDisposition, bool> NonReadyResolutions() => new()
    {
        { new StorageRouteSnapshotResolution.Missing(), RoutedDestinationDisposition.NoRoute, true },
        { new StorageRouteSnapshotResolution.RouteNotActivated(), RoutedDestinationDisposition.RouteNotActivated, true },
        { new StorageRouteSnapshotResolution.RouteNotActive(), RoutedDestinationDisposition.RouteNotActive, false },
        { new StorageRouteSnapshotResolution.ProfileNotActive(), RoutedDestinationDisposition.ProfileNotActive, false },
        { new StorageRouteSnapshotResolution.RouteRevisionMissing(), RoutedDestinationDisposition.Invalid, false },
        { new StorageRouteSnapshotResolution.ProfileMissing(), RoutedDestinationDisposition.Invalid, false },
        { new StorageRouteSnapshotResolution.ProfileRevisionMissing(), RoutedDestinationDisposition.Invalid, false },
        { new StorageRouteSnapshotResolution.Invalid(StorageRouteSnapshotInvalidReason.ProfileRevisionMode), RoutedDestinationDisposition.Invalid, false },
        { new StorageRouteSnapshotResolution.Cancelled(), RoutedDestinationDisposition.ResolutionFailed, false },
    };

    [Fact]
    public async Task Caller_cancellation_is_not_downgraded_into_a_storage_policy_verdict()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var routes = new StubRouteResolver(new StorageRouteSnapshotResolution.Cancelled());

        await Should.ThrowAsync<OperationCanceledException>(
            () => new RoutedDestinationResolver(routes).ResolveAsync(new SyntheticDataClass(), Guid.NewGuid(), cancellation.Token));
    }

    /// <summary>
    /// The asymmetry PR #1519 could only state in a comment, now stated where the shared resolver reads it. The main
    /// artifact plane has a local blob backend to keep; Agent Run log capture has none, so an un-activated route must
    /// stay a typed refusal there rather than become silently-dropped capture.
    /// </summary>
    [Fact]
    public void The_two_shipped_declarations_carry_the_asymmetry_their_resolvers_used_to_hand_write()
    {
        new WorkflowArtifactDataClass().ShouldBeAssignableTo<IRoutedDataClassLocalFallback>();
        new AgentRunLogDataClass().ShouldNotBeAssignableTo<IRoutedDataClassLocalFallback>();
    }

    private static async Task<RoutedDestination> ResolveAsync(IRoutedDataClass dataClass, StorageRouteSnapshotResolution resolution)
    {
        var routes = new StubRouteResolver(resolution);
        var destination = await new RoutedDestinationResolver(routes).ResolveAsync(dataClass, Guid.NewGuid(), CancellationToken.None);

        routes.Requests.Count.ShouldBe(1, "the shared resolver reads routing state exactly once; repair is a consumer's own step");

        return destination;
    }

    private static StorageRouteSnapshot Snapshot(Guid profileId, int profileRevision) => new()
    {
        RouteId = Guid.NewGuid(), RouteRevision = 4, DataClassTypeKey = SyntheticKey,
        StorageProfileId = profileId, StorageProfileRevision = profileRevision, ProviderTypeKey = "local-rwx/v1",
        NamespaceFingerprint = $"sha256:{new string('a', 64)}",
    };

    /// <summary>A third routed data class, declared and nothing more. No local home, so it refuses until it is cut over.</summary>
    private sealed record SyntheticDataClass : IRoutedDataClass
    {
        public string TypeKey => SyntheticKey;

        public string DisplayName => "Synthetic probe";
    }

    /// <summary>The same declaration plus the one optional capability, which is the only thing that changes its policy.</summary>
    private sealed record SyntheticDataClassWithLocalHome : IRoutedDataClass, IRoutedDataClassLocalFallback
    {
        public string TypeKey => SyntheticKey;

        public string DisplayName => "Synthetic probe with a local home";
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
