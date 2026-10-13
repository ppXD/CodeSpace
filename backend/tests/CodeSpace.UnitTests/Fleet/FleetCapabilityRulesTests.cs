using CodeSpace.Core.Services.Fleet;
using CodeSpace.Messages.Fleet;
using Shouldly;

namespace CodeSpace.UnitTests.Fleet;

/// <summary>
/// The fleet gate's one decision: a capability is usable only when there is a live job server and EVERY live one
/// advertises it. A server that stopped heartbeating is gone (Hangfire's own watchdog may not have removed its row yet),
/// a server whose heartbeat was never recorded is assumed alive, and a fleet with no live server holds every capability
/// back: the next worker to join may be of either build — the gap a worker rollback opens between stopping this
/// build's workers and starting the previous build's, while the API still stages forks.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FleetCapabilityRulesTests
{
    private const string Marker = FleetCapabilityMarkers.SealedPayloadV1;
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("all-new", true)]
    [InlineData("mixed", false)]
    [InlineData("old-only", false)]
    [InlineData("old-stale-new-live", true)]
    [InlineData("old-live-new-stale", false)]
    [InlineData("old-at-window-edge", false)]
    [InlineData("old-just-past-window", true)]
    [InlineData("old-never-heartbeat", false)]
    [InlineData("only-stale", false)]
    [InlineData("empty", false)]
    [InlineData("newer-marker-only", false)]
    [InlineData("marker-case-differs", false)]
    public void A_capability_holds_only_when_every_live_server_advertises_it(string fleet, bool expected) =>
        FleetCapabilityRules.EveryLiveServerAdvertises(Fleet(fleet), Marker, Now).ShouldBe(expected);

    [Fact]
    public void The_live_servers_lacking_a_capability_are_named()
    {
        var servers = new[]
        {
            Server("old-live", Now.AddSeconds(-10), "default"),
            Server("old-stale", Now.AddMinutes(-10), "default"),
            Server("new-live", Now.AddSeconds(-5), "default", Marker),
        };

        FleetCapabilityRules.LiveServersLacking(servers, Marker, Now).ShouldBe(["old-live"]);
    }

    [Fact]
    public void The_marker_and_the_liveness_window_are_pinned()
    {
        // The marker is compared across builds: a renamed marker makes every new worker look like a previous-version one
        // to its peers, and sealing never resumes. A new payload shape is a NEW marker, never an edited one.
        FleetCapabilityMarkers.SealedPayloadV1.ShouldBe("cap-sealed-payload-v1");
        FleetCapabilityMarkers.Advertised.ShouldContain(FleetCapabilityMarkers.SealedPayloadV1);

        // Four Hangfire heartbeats (30s each): long enough that a busy server is never mistaken for a gone one, short
        // enough that a killed previous-version worker stops holding sealing back within minutes.
        FleetCapabilityRules.LiveHeartbeatWindow.ShouldBe(TimeSpan.FromMinutes(2));
    }

    private static IReadOnlyList<WorkerServerHeartbeat> Fleet(string name) => name switch
    {
        "all-new" => [Server("new-a", Now.AddSeconds(-5), "default", Marker), Server("new-b", Now.AddSeconds(-20), "agents", Marker)],
        "mixed" => [Server("new", Now.AddSeconds(-5), "default", Marker), Server("old", Now.AddSeconds(-5), "default")],
        "old-only" => [Server("old", Now.AddSeconds(-5), "default")],
        "old-stale-new-live" => [Server("new", Now.AddSeconds(-5), "default", Marker), Server("old", Now.AddMinutes(-3), "default")],
        "old-live-new-stale" => [Server("new", Now.AddMinutes(-3), "default", Marker), Server("old", Now.AddSeconds(-5), "default")],
        "old-at-window-edge" => [Server("new", Now, "default", Marker), Server("old", Now - FleetCapabilityRules.LiveHeartbeatWindow, "default")],
        "old-just-past-window" => [Server("new", Now, "default", Marker), Server("old", Now - FleetCapabilityRules.LiveHeartbeatWindow - TimeSpan.FromSeconds(1), "default")],
        "old-never-heartbeat" => [Server("new", Now, "default", Marker), Server("old", null, "default")],
        "only-stale" => [Server("old", Now.AddMinutes(-30), "default")],
        "empty" => [],
        "newer-marker-only" => [Server("future", Now, "default", "cap-sealed-payload-v2")],
        "marker-case-differs" => [Server("odd", Now, "default", Marker.ToUpperInvariant())],
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    private static WorkerServerHeartbeat Server(string id, DateTime? heartbeat, params string[] queues) => new(id, queues, heartbeat);
}
