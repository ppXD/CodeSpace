using System.Collections.Concurrent;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Fleet;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Fleet;

/// <summary>
/// <see cref="IFleetCapabilities"/> over a snapshot of the fleet registry that is re-read at most once per
/// <see cref="SnapshotLifetime"/>: the gate sits on write paths, the registry is a database. The answer can lag a
/// fleet change by one lifetime and never leads it — a previous-version worker that just left still holds the new
/// shape back for up to that long, which only costs writing the shape it read.
///
/// <para>A fleet that cannot be read answers false: the gate cannot prove no previous-version worker is in it, and the
/// shape every worker reads is the one a write can always fall back to. A host whose registry observes no fleet at all
/// (no job storage — a test host) answers true without reading.</para>
/// </summary>
public sealed class FleetCapabilities : IFleetCapabilities, ISingletonDependency
{
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(30);

    private readonly IWorkerFleetRegistry _registry;
    private readonly TimeProvider _clock;
    private readonly ILogger<FleetCapabilities> _logger;
    private readonly Lock _refresh = new();
    private readonly ConcurrentDictionary<string, bool> _lastAnswers = new(StringComparer.Ordinal);
    private FleetSnapshot? _snapshot;

    public FleetCapabilities(IWorkerFleetRegistry registry, TimeProvider clock, ILogger<FleetCapabilities> logger)
    {
        _registry = registry;
        _clock = clock;
        _logger = logger;
    }

    public bool AllWorkersSupport(string capability)
    {
        if (!_registry.ObservesFleet) return true;

        var snapshot = CurrentSnapshot();
        var supported = snapshot.Servers is { } servers && FleetCapabilityRules.EveryLiveServerAdvertises(servers, capability, snapshot.ReadAtUtc);

        ReportChange(capability, supported, snapshot);

        return supported;
    }

    private FleetSnapshot CurrentSnapshot()
    {
        if (Fresh(Volatile.Read(ref _snapshot)) is { } fresh) return fresh;

        lock (_refresh)
        {
            if (Fresh(_snapshot) is { } raced) return raced;

            var read = ReadSnapshot();
            Volatile.Write(ref _snapshot, read);

            return read;
        }
    }

    private FleetSnapshot? Fresh(FleetSnapshot? snapshot) => snapshot is not null && _clock.GetUtcNow().UtcDateTime - snapshot.ReadAtUtc < SnapshotLifetime ? snapshot : null;

    private FleetSnapshot ReadSnapshot()
    {
        var readAt = _clock.GetUtcNow().UtcDateTime;

        try
        {
            return new FleetSnapshot(readAt, _registry.ReadServers());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The worker fleet could not be read; new data shapes are held back until it can be");
            return new FleetSnapshot(readAt, null);
        }
    }

    /// <summary>Logged on a change of answer only — a rolling deploy flips each capability twice, not once per write.</summary>
    private void ReportChange(string capability, bool supported, FleetSnapshot snapshot)
    {
        var previous = _lastAnswers.TryGetValue(capability, out var last) ? last : (bool?)null;

        if (previous == supported) return;

        _lastAnswers[capability] = supported;

        if (!supported)
            _logger.LogWarning("Not every live worker advertises {Capability}, or none is live; writing the shape a previous-version worker reads until they all do. Holding it back: {Servers}", capability, HoldingBack(capability, snapshot));
        else if (previous == false)
            _logger.LogInformation("Every live worker now advertises {Capability}", capability);
    }

    private static IReadOnlyList<string> HoldingBack(string capability, FleetSnapshot snapshot)
    {
        if (snapshot.Servers is not { } servers) return ["<fleet unreadable>"];

        var lacking = FleetCapabilityRules.LiveServersLacking(servers, capability, snapshot.ReadAtUtc);

        return lacking.Count > 0 ? lacking : ["<no live worker>"];
    }

    /// <summary>The registry as read at <see cref="ReadAtUtc"/>; null servers when the read failed.</summary>
    private sealed record FleetSnapshot(DateTime ReadAtUtc, IReadOnlyList<WorkerServerHeartbeat>? Servers);
}
