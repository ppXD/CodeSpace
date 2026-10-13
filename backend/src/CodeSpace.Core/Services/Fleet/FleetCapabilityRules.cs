using CodeSpace.Messages.Fleet;

namespace CodeSpace.Core.Services.Fleet;

/// <summary>
/// The fleet gate's decision, pure: a capability holds when there is a LIVE job server and every live one advertises
/// its marker. A server whose heartbeat is older than <see cref="LiveHeartbeatWindow"/> is gone — Hangfire's own
/// watchdog removes its row only after its server timeout — and a server with no recorded heartbeat is assumed alive.
/// A fleet with no live server holds every capability back: the API pod asks too (a replay or rerun fork), and the next
/// worker to join may be of either build — the gap a worker rollback opens between stopping this build's workers and
/// starting the previous build's.
/// </summary>
public static class FleetCapabilityRules
{
    /// <summary>Four Hangfire heartbeats (30s apart): a busy server is never mistaken for a gone one, and a killed previous-version worker stops holding a capability back within minutes.</summary>
    public static readonly TimeSpan LiveHeartbeatWindow = TimeSpan.FromMinutes(2);

    public static bool EveryLiveServerAdvertises(IReadOnlyCollection<WorkerServerHeartbeat> servers, string capability, DateTime nowUtc) =>
        servers.Any(server => IsLive(server, nowUtc)) && LiveServersLacking(servers, capability, nowUtc).Count == 0;

    /// <summary>The ids of the live servers that do not advertise <paramref name="capability"/> — the ones holding it back.</summary>
    public static IReadOnlyList<string> LiveServersLacking(IReadOnlyCollection<WorkerServerHeartbeat> servers, string capability, DateTime nowUtc) =>
        servers.Where(server => IsLive(server, nowUtc) && !Advertises(server, capability)).Select(server => server.ServerId).ToList();

    private static bool IsLive(WorkerServerHeartbeat server, DateTime nowUtc) => server.LastHeartbeatUtc is not { } heartbeat || nowUtc - heartbeat <= LiveHeartbeatWindow;

    private static bool Advertises(WorkerServerHeartbeat server, string capability) => server.Queues.Contains(capability, StringComparer.Ordinal);
}
