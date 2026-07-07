using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: the P1.3 grading-heartbeat loop (<see cref="SupervisorTurnService.RunGradingHeartbeatLoopAsync"/>) —
/// pins the cancellation contract that keeps a long acceptance grade from looking abandoned to the reconciler
/// WITHOUT waiting out the real 90s production interval (a millisecond-scale interval drives the same code path).
/// The DB-observable effect (a real ledger row landing, and the reconciler reading it as liveness) is proved at
/// the integration tier — this pins the pure loop mechanics: it logs repeatedly while un-cancelled, stops the
/// instant it's cancelled (mid-sleep or between ticks), and never lets <c>OperationCanceledException</c> escape.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorGradingHeartbeatTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private const string NodeId = "sup";

    // RunGradingHeartbeatLoopAsync touches ONLY _recordLogger — every other dependency is stored by the ctor
    // (plain field assignment, no eager calls) and never read on this path, so null! is safe here exactly as the
    // existing SupervisorTurnServiceTests already pass null! for db/offloader on paths that don't touch them.
    private static SupervisorTurnService Service(IRunRecordLogger logger) =>
        new(null!, null!, null!, db: null!, null!, null!, null!, null!, null!, logger, null!, NullLogger<SupervisorTurnService>.Instance);

    [Fact]
    public async Task The_loop_logs_repeatedly_while_uncancelled()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();

        var loop = Service(logger).RunGradingHeartbeatLoopAsync(RunId, NodeId, TimeSpan.FromMilliseconds(15), cts.Token);

        // Let several ticks land, then stop — proves it's a REPEATING loop, not a one-shot.
        await Task.Delay(TimeSpan.FromMilliseconds(80));
        cts.Cancel();
        await loop;

        logger.Calls.Count.ShouldBeGreaterThanOrEqualTo(3, "an 80ms window at a 15ms interval must land several heartbeats");
        logger.Calls.ShouldAllBe(c => c.RunId == RunId && c.NodeId == NodeId && c.Level == LogLevel.Info);
    }

    [Fact]
    public async Task Cancelling_stops_the_loop_without_throwing()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();

        var loop = Service(logger).RunGradingHeartbeatLoopAsync(RunId, NodeId, TimeSpan.FromMilliseconds(10), cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(30));
        cts.Cancel();

        // Must complete cleanly — OperationCanceledException is caught INSIDE the loop, never surfaced to the caller
        // (the P1.3 call site's finally-block await must never itself need a try/catch for this).
        await Should.NotThrowAsync(() => loop);
    }

    [Fact]
    public async Task An_already_cancelled_token_produces_zero_heartbeats()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Service(logger).RunGradingHeartbeatLoopAsync(RunId, NodeId, TimeSpan.FromMilliseconds(10), cts.Token);

        logger.Calls.ShouldBeEmpty("a grade that finishes before the FIRST tick never needs a heartbeat");
    }

    /// <summary>Minimal <see cref="IRunRecordLogger"/> fake — every method a harmless no-op except <see cref="LogAsync"/>, which records each call. The grading-heartbeat path touches ONLY LogAsync; every other member exists solely to satisfy the interface.</summary>
    private sealed class RecordingLogger : IRunRecordLogger
    {
        public List<(Guid RunId, string? NodeId, LogLevel Level, string Message)> Calls { get; } = new();

        public Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken cancellationToken)
        {
            Calls.Add((runId, nodeId, level, message));
            return Task.CompletedTask;
        }

        public Task RunQueuedAsync(Guid runId, string sourceType, Guid? actorId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunStartedAsync(Guid runId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseLoadedAsync(Guid runId, int version, string definitionHash, int nodeCount, int edgeCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ScopeResolvedAsync(Guid runId, int wfCount, int teamCount, int sysCount, int secretPathCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task VariablesSnapshottedAsync(Guid runId, int wfCount, int teamCount, string releaseHash, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunCompletedAsync(Guid runId, TimeSpan duration, bool outputsPresent, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunFailedAsync(Guid runId, string error, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunCancelledAsync(Guid runId, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RunReplayedAsync(Guid runId, Guid? parentRunId, int snapshotCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SupervisorRunRecoveredAsync(Guid runId, int attempt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Guid> NodeStartedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> resolvedInputs, IReadOnlyDictionary<string, JsonElement> resolvedConfig, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
        public Task NodeCompletedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeFailedAsync(Guid runId, string nodeId, string iterationKey, string error, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AttemptFailedAsync(Guid runId, string nodeId, string iterationKey, int attempt, int maxAttempts, string error, TimeSpan duration, double retryInSeconds, Guid? parentRecordId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeSkippedAsync(Guid runId, string nodeId, string iterationKey, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeSuspendedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, DateTimeOffset? wakeAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IterationStartedAsync(Guid runId, string nodeId, int itemCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IterationCompletedAsync(Guid runId, string nodeId, int itemCount, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<(Guid RecordId, Guid CorrelationId)> ExternalCallStartedAsync(Guid runId, string? nodeId, string target, string method, JsonElement? requestPayload, Guid? parentRecordId, CancellationToken cancellationToken) => Task.FromResult((Guid.NewGuid(), Guid.NewGuid()));
        public Task ExternalCallCompletedAsync(Guid runId, string? nodeId, Guid correlationId, int? statusCode, JsonElement? responsePayload, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExternalCallFailedAsync(Guid runId, string? nodeId, Guid correlationId, string target, string error, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WaitReissuedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, Guid waitId, Guid byUserId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Guid> RecordInteractionAsync(Guid runId, string recordType, string? nodeId, string iterationKey, Guid correlationId, Guid? parentRecordId, JsonElement payload, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
    }
}
