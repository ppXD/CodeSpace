namespace CodeSpace.Messages.Fleet;

/// <summary>
/// One job server of the worker fleet as the fleet registry last recorded it: its id, the queues it listens on — where
/// a capability marker rides, never enqueued to (<c>FleetCapabilityMarkers</c>) — and its last heartbeat in UTC. A
/// null heartbeat means the registry recorded none; it is treated as alive.
/// </summary>
public sealed record WorkerServerHeartbeat(string ServerId, IReadOnlyList<string> Queues, DateTime? LastHeartbeatUtc);
