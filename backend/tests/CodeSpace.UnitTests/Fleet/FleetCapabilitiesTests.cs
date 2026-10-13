using CodeSpace.Core.Services.Fleet;
using CodeSpace.Messages.Fleet;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace CodeSpace.UnitTests.Fleet;

/// <summary>
/// The gate every sealing write asks: read the fleet at most once per snapshot lifetime (a seal is a hot path, the
/// registry is a database), see a fleet change once the snapshot ages out, and answer with the shape every worker can
/// read when the fleet cannot be read at all.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FleetCapabilitiesTests
{
    private const string Marker = FleetCapabilityMarkers.SealedPayloadV1;

    [Fact]
    public void The_fleet_is_read_once_per_snapshot_lifetime()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var registry = new ScriptedRegistry(clock, () => [Live("new", Marker)]);
        var fleet = new FleetCapabilities(registry, clock, NullLogger<FleetCapabilities>.Instance);

        fleet.AllWorkersSupport(Marker).ShouldBeTrue();
        clock.Advance(FleetCapabilities.SnapshotLifetime - TimeSpan.FromSeconds(1));
        fleet.AllWorkersSupport(Marker).ShouldBeTrue();

        registry.Reads.ShouldBe(1, "a second ask inside the snapshot lifetime must not hit the registry again");

        clock.Advance(TimeSpan.FromSeconds(1));
        fleet.AllWorkersSupport(Marker).ShouldBeTrue();

        registry.Reads.ShouldBe(2, "an aged-out snapshot is re-read");
    }

    [Fact]
    public void A_previous_version_worker_holds_the_capability_back_until_it_is_gone_and_the_snapshot_ages_out()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var oldWorkerAlive = true;
        var registry = new ScriptedRegistry(clock, () => oldWorkerAlive ? [Live("new", Marker), Live("old", "default")] : [Live("new", Marker)]);
        var fleet = new FleetCapabilities(registry, clock, NullLogger<FleetCapabilities>.Instance);

        fleet.AllWorkersSupport(Marker).ShouldBeFalse("a live previous-version worker reads only the legacy shape");

        oldWorkerAlive = false;
        fleet.AllWorkersSupport(Marker).ShouldBeFalse("the cached snapshot still holds the old worker — the answer may lag by one lifetime, never lead");

        clock.Advance(FleetCapabilities.SnapshotLifetime);
        fleet.AllWorkersSupport(Marker).ShouldBeTrue();
    }

    [Fact]
    public void An_unreadable_fleet_answers_with_the_shape_every_worker_reads()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var registry = new ScriptedRegistry(clock, () => throw new InvalidOperationException("storage down"));
        var fleet = new FleetCapabilities(registry, clock, NullLogger<FleetCapabilities>.Instance);

        Should.NotThrow(() => fleet.AllWorkersSupport(Marker)).ShouldBeFalse(
            "a gate that cannot see the fleet cannot prove no previous-version worker is in it; the caller must still be able to write");
    }

    [Fact]
    public void A_fleet_with_no_live_worker_holds_every_capability_back()
    {
        // The API pod asks too (a replay or rerun fork). With every current worker stopped for a rollback, the next worker
        // to join may be a previous-version one, and the fork it picks up must be the shape it reads.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var fleet = new FleetCapabilities(new ScriptedRegistry(clock, () => []), clock, NullLogger<FleetCapabilities>.Instance);

        fleet.AllWorkersSupport(Marker).ShouldBeFalse();
    }

    [Fact]
    public void A_host_with_no_job_storage_observes_no_fleet_and_holds_nothing_back()
    {
        // Every deployed role registers job storage; a host without it runs no job and shares its rows with no worker.
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
        var registry = new ScriptedRegistry(clock, () => [Live("old", "default")]) { ObservesFleet = false };
        var fleet = new FleetCapabilities(registry, clock, NullLogger<FleetCapabilities>.Instance);

        fleet.AllWorkersSupport(Marker).ShouldBeTrue();
        registry.Reads.ShouldBe(0);
    }

    [Fact]
    public void The_hangfire_registry_observes_a_fleet_only_over_job_storage() => new HangfireWorkerFleetRegistry().ObservesFleet.ShouldBeFalse();

    private static Func<DateTimeOffset, WorkerServerHeartbeat> Live(string id, params string[] queues) => now => new WorkerServerHeartbeat(id, queues, now.UtcDateTime);

    /// <summary>A registry whose servers heartbeat "now" by the test clock, so only presence decides liveness here; staleness is FleetCapabilityRulesTests' job.</summary>
    private sealed class ScriptedRegistry(TimeProvider clock, Func<IReadOnlyList<Func<DateTimeOffset, WorkerServerHeartbeat>>> servers) : IWorkerFleetRegistry
    {
        public int Reads { get; private set; }

        public bool ObservesFleet { get; init; } = true;

        public IReadOnlyList<WorkerServerHeartbeat> ReadServers()
        {
            Reads++;
            return servers().Select(server => server(clock.GetUtcNow())).ToList();
        }
    }
}
