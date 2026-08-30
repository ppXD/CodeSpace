using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows.Artifacts.Runtime;

/// <summary>
/// Which destinations the scheduled sweep considers its business.
///
/// <para>The sweep's own stated set is "exactly the set whose failure loses data", and a profile does not stop holding
/// bytes because someone disabled or retired it. Filtering the population to Active profiles left the two destinations
/// that lose the most unwatched: the one an Active route still binds writes to that no write can land on, and the one
/// nobody writes to any more that every stored object still lives on.</para>
///
/// <para>Pinned over in-memory tables (InternalsVisibleTo) so the admission rule is a unit-level contract; the
/// integration suite proves the same query translates and probes.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class StorageDestinationHealthSweepPopulationTests
{
    private readonly Guid _teamId = Guid.NewGuid();
    private readonly Guid _profileId = Guid.NewGuid();
    private readonly Guid _profileRevisionId = Guid.NewGuid();
    private readonly Guid _routeId = Guid.NewGuid();

    [Fact]
    public void An_active_route_bound_to_a_disabled_profile_is_still_swept()
    {
        // Disabling a profile unbinds no route. Every write still resolves here and every one of them now fails.
        var tables = Tables(StorageProfileState.Disabled, StorageRouteState.Active, placement: null);

        Swept(tables).ShouldBe(new[] { _profileId });
    }

    [Theory]
    [InlineData(ArtifactLocationState.Missing, true)]    // the bytes are gone, and that is still a record of this destination
    [InlineData(ArtifactLocationState.Corrupt, true)]    // the destination holds something else, which is not nothing
    [InlineData(ArtifactLocationState.Available, true)]  // the ordinary case: bytes a reader will ask for
    [InlineData(ArtifactLocationState.Purged, false)]    // settled: the destination holds nothing on this row's behalf
    [InlineData(ArtifactLocationState.Deleted, false)]
    public void A_retired_profile_is_swept_for_as_long_as_it_holds_an_unsettled_placement(ArtifactLocationState state, bool swept)
    {
        var tables = Tables(StorageProfileState.Retired, StorageRouteState.Draft, state);

        Swept(tables).ShouldBe(swept ? new[] { _profileId } : []);
    }

    [Fact]
    public void A_profile_that_holds_nothing_and_that_no_active_route_names_is_left_alone()
    {
        // The cost side of the trade: a real provider round trip per pass, spent on a destination no run can reach
        // and no stored object depends on.
        var tables = Tables(StorageProfileState.Active, StorageRouteState.Draft, placement: null);

        Swept(tables).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(StorageProfileState.Active, true)]
    [InlineData(StorageProfileState.Disabled, false)]
    [InlineData(StorageProfileState.Retired, false)]
    public void What_the_pass_verifies_follows_what_the_profile_still_admits(StorageProfileState state, bool verifyWrite)
    {
        // Widening the population bought nothing while every pass asked to verify a WRITE: the lifecycle gate refuses
        // that before any driver opens, so the row recorded for a Disabled or Retired destination restated
        // storage_profile.state and never observed the destination at all. A read is admitted in every state.
        var tables = Tables(state, StorageRouteState.Active, ArtifactLocationState.Available);

        Destinations(tables).Single().VerifyWrite.ShouldBe(verifyWrite);
    }

    [Fact]
    public void Never_observed_goes_first_then_longest_unobserved()
    {
        // What makes a cap safe rather than starving. Nulls are ordered first explicitly because PostgreSQL sorts
        // them LAST on an ascending key — left to the provider, the destinations nothing has ever contacted would
        // queue behind every destination that has.
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromHours(9);
        var tables = ManyTables(
            Candidate("probed-recently", observedAt: stale + TimeSpan.FromHours(3), createdAt: Old()),
            Candidate("never-probed-oldest", observedAt: null, createdAt: Old()),
            Candidate("probed-longest-ago", observedAt: stale, createdAt: Old()),
            Candidate("never-probed-newest", observedAt: null, createdAt: DateTimeOffset.UtcNow));

        Named(tables).ShouldBe(["never-probed-newest", "never-probed-oldest", "probed-longest-ago", "probed-recently"]);
    }

    [Fact]
    public void More_destinations_are_due_than_one_pass_takes_and_it_takes_exactly_the_cap()
    {
        // The population only ever grows, so an unbounded pass spends one more provider round trip for every
        // destination the deployment ever adds. The cap is asserted at the value the sweep actually runs under — not
        // one a test chose — so that wiring MaxPerPass to the query is itself what this pins. Coverage then degrades
        // to "slower": the two destinations left over are the two most recently observed, and they lead the next pass.
        var overflowing = ManyTables([.. NeverProbed(2), .. ProbedOldestFirst(StorageDestinationHealthSweep.MaxPerPass)]);

        var probed = Named(overflowing);

        probed.Count.ShouldBe(StorageDestinationHealthSweep.MaxPerPass, "the cap bounds how many probes one pass makes; it is the only bound this sweep has");
        probed.Take(2).ShouldBe(["never-probed-1", "never-probed-0"], "a destination nothing has ever contacted is the one most likely to be wrong");
        probed[2].ShouldBe("probed-000", "after the never-probed comes the longest unobserved");
        probed.ShouldNotContain("probed-198");
        probed.ShouldNotContain("probed-199", "the freshest observations are the ones a full pass defers");
    }

    // ─── Tables ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<Guid> Swept(StorageDestinationHealthSweep.PopulationTables tables) =>
        Destinations(tables).Select(destination => destination.StorageProfileId).ToList();

    private static IReadOnlyList<StorageDestinationHealthSweep.MonitoredDestination> Destinations(StorageDestinationHealthSweep.PopulationTables tables) =>
        StorageDestinationHealthSweep.StaleDestinations(tables, DateTimeOffset.UtcNow).ToList();

    /// <summary>The stable names of the destinations one bounded pass takes, in the order it takes them.</summary>
    private static IReadOnlyList<string> Named(StorageDestinationHealthSweep.PopulationTables tables)
    {
        var names = tables.Profiles.ToDictionary(profile => profile.Id, profile => profile.StableName);

        return [.. Destinations(tables).Select(destination => names[destination.StorageProfileId])];
    }

    /// <summary>Destinations nothing has ever contacted. The higher-numbered is the newer, so it leads.</summary>
    private static IEnumerable<SweepCandidate> NeverProbed(int count) =>
        Enumerable.Range(0, count).Select(index => Candidate($"never-probed-{index}", observedAt: null, createdAt: Old() + TimeSpan.FromMinutes(index)));

    /// <summary>Destinations all stale enough to be due, <c>probed-000</c> the longest ago and the last one the freshest.</summary>
    private static IEnumerable<SweepCandidate> ProbedOldestFirst(int count)
    {
        var now = DateTimeOffset.UtcNow;

        return Enumerable.Range(0, count).Select(index => Candidate($"probed-{index:D3}", now - TimeSpan.FromMinutes(count - index), Old()));
    }

    private static DateTimeOffset Old() => DateTimeOffset.UtcNow - TimeSpan.FromDays(30);

    /// <summary>One profile in the population — it holds an unsettled placement — with its own observation age and birth.</summary>
    private static SweepCandidate Candidate(string stableName, DateTimeOffset? observedAt, DateTimeOffset createdAt) =>
        new(Guid.NewGuid(), Guid.NewGuid(), stableName, observedAt, createdAt);

    private StorageDestinationHealthSweep.PopulationTables ManyTables(params SweepCandidate[] candidates) => new()
    {
        Profiles = candidates.Select(candidate => new StorageProfile
        {
            Id = candidate.ProfileId, TeamId = _teamId, StableName = candidate.StableName,
            CurrentRevision = 1, State = StorageProfileState.Active, CreatedDate = candidate.CreatedAt,
        }).AsQueryable(),
        Routes = Rows<StorageRoute>(),
        RouteRevisions = Rows<StorageRouteRevision>(),
        ProfileRevisions = candidates.Select(candidate => new StorageProfileRevision
        {
            Id = candidate.RevisionId, TeamId = _teamId, StorageProfileId = candidate.ProfileId, Revision = 1,
        }).AsQueryable(),
        Locations = candidates.Select(candidate => new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = _teamId, StorageProfileRevisionId = candidate.RevisionId, State = ArtifactLocationState.Available,
        }).AsQueryable(),
        Health = candidates.Where(candidate => candidate.ObservedAt != null).Select(candidate => new StorageProfileHealth
        {
            TeamId = _teamId, StorageProfileId = candidate.ProfileId, ProfileRevision = 1, ObservedAt = candidate.ObservedAt!.Value,
        }).AsQueryable(),
    };

    private sealed record SweepCandidate(Guid ProfileId, Guid RevisionId, string StableName, DateTimeOffset? ObservedAt, DateTimeOffset CreatedAt);

    private StorageDestinationHealthSweep.PopulationTables Tables(StorageProfileState profileState, StorageRouteState routeState, ArtifactLocationState? placement) => new()
    {
        Profiles = Rows(new StorageProfile { Id = _profileId, TeamId = _teamId, StableName = "sweep", CurrentRevision = 1, State = profileState }),
        Routes = Rows(new StorageRoute { Id = _routeId, TeamId = _teamId, DataClassTypeKey = "agent-run-log/v1", CurrentRevision = 1, State = routeState }),
        RouteRevisions = Rows(new StorageRouteRevision { Id = Guid.NewGuid(), TeamId = _teamId, StorageRouteId = _routeId, Revision = 1, StorageProfileId = _profileId }),
        ProfileRevisions = Rows(new StorageProfileRevision { Id = _profileRevisionId, TeamId = _teamId, StorageProfileId = _profileId, Revision = 1 }),
        Locations = placement is { } state
            ? Rows(new ArtifactLocation { Id = Guid.NewGuid(), TeamId = _teamId, StorageProfileRevisionId = _profileRevisionId, State = state })
            : Rows<ArtifactLocation>(),
        Health = Rows<StorageProfileHealth>(),
    };

    private static IQueryable<T> Rows<T>(params T[] rows) => rows.AsQueryable();
}
