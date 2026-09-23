using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: the P1.3 grading-heartbeat loop (<see cref="SupervisorTurnService.RunGradingHeartbeatLoopAsync"/>) —
/// pins the cancellation contract that keeps a long acceptance grade from looking abandoned to the reconciler
/// WITHOUT waiting out the real 90s production interval: a <see cref="FakeTimeProvider"/> decides when each interval
/// has elapsed, so the production value itself costs nothing. The DB-observable effect (a real ledger row landing,
/// and the reconciler reading it as liveness) is proved at the integration tier — this pins the pure loop mechanics:
/// it logs once per elapsed interval while un-cancelled, stops the instant it's cancelled (mid-sleep or between
/// ticks), and never lets <c>OperationCanceledException</c> escape.
///
/// <para>These once slept on the wall clock — "three 15ms ticks within 10s", "cancel after 30ms" — which proved the
/// loop repeats only as reliably as the runner happened to schedule it. On the fake clock the same properties are
/// exact: one heartbeat per interval, not "at least three".</para>
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
        new(null!, null!, null!, db: Infrastructure.EmptyTestDb.New(), null!, null!, null!, null!, null!, logger, null!, null!, null!, new NullCompletionComposer(), null!, null!, NullLogger<SupervisorTurnService>.Instance);

    [Fact]
    public async Task The_loop_logs_once_per_elapsed_interval_while_uncancelled()
    {
        var time = new FakeTimeProvider();
        var interval = SupervisorLane.AcceptanceGradeHeartbeatInterval;
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();

        var loop = Service(logger).RunGradingHeartbeatLoopAsync(RunId, NodeId, interval, cts.Token, time);

        logger.Calls.ShouldBeEmpty("the first heartbeat is owed only once a full interval of grading has passed");

        // Three OBSERVED ticks prove it is a REPEATING loop, not a one-shot — and on the fake clock the count after
        // i intervals is exactly i, where the wall clock could only ever promise "at least".
        for (var i = 1; i <= 3; i++)
        {
            await AdvanceUntilLoggedAsync(time, logger, interval, i);

            logger.Calls.Count.ShouldBe(i, $"exactly one heartbeat per elapsed interval — after {i} interval(s) there must be {i}");
        }

        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        logger.Calls.ShouldAllBe(c => c.RunId == RunId && c.NodeId == NodeId && c.Level == LogLevel.Info);
    }

    [Fact]
    public async Task Cancelling_stops_the_loop_without_throwing()
    {
        var time = new FakeTimeProvider();
        var interval = SupervisorLane.AcceptanceGradeHeartbeatInterval;
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();

        var loop = Service(logger).RunGradingHeartbeatLoopAsync(RunId, NodeId, interval, cts.Token, time);

        // One tick first, so the cancel below lands MID-SLEEP on the next interval rather than before the loop ran.
        await AdvanceUntilLoggedAsync(time, logger, interval, 1);

        cts.Cancel();

        // Must complete cleanly — OperationCanceledException is caught INSIDE the loop, never surfaced to the caller
        // (the P1.3 call site's finally-block await must never itself need a try/catch for this). Recorded rather than
        // asserted with Should.NotThrowAsync, which passes a CANCELED task without a word and so could never catch the
        // very escape this test is named for. Bounded, so a loop that ignores the cancel mid-sleep fails, not hangs.
        var escaped = await Record.ExceptionAsync(() => loop.WaitAsync(TimeSpan.FromSeconds(10)));

        escaped.ShouldBeNull("a cancel mid-sleep must end the loop at once and quietly — a TimeoutException means it outlived the grade it protects, a TaskCanceledException that the cancel escaped to the caller");

        time.Advance(interval * 3);
        (await logger.Logged.WaitAsync(TimeSpan.FromMilliseconds(200))).ShouldBeFalse("a cancelled loop logs no more heartbeats, however much time passes");
        logger.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_already_cancelled_token_produces_zero_heartbeats()
    {
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Service(logger).RunGradingHeartbeatLoopAsync(RunId, NodeId, SupervisorLane.AcceptanceGradeHeartbeatInterval, cts.Token, new FakeTimeProvider()).WaitAsync(TimeSpan.FromSeconds(10));

        logger.Calls.ShouldBeEmpty("a grade that finishes before the FIRST tick never needs a heartbeat");
    }

    /// <summary>
    /// Advances the fake clock until the loop logs heartbeat <paramref name="ordinal"/>, rather than advancing one
    /// whole interval and assuming the loop was already listening.
    ///
    /// <para>The loop arms its next delay only AFTER the previous heartbeat's write returns, so a single Advance can
    /// land before that registration and be missed — the clock then never moves again. Nudging in tenths of an
    /// interval cannot fire a delay early, so the exact count asserted at the call site still means one heartbeat
    /// per interval. Same shape as <c>HeartbeatLoopTests</c>.</para>
    /// </summary>
    private static async Task AdvanceUntilLoggedAsync(FakeTimeProvider time, RecordingLogger logger, TimeSpan interval, int ordinal)
    {
        for (var nudge = 0; nudge < 200; nudge++)
        {
            if (await logger.Logged.WaitAsync(TimeSpan.FromMilliseconds(10))) return;

            time.Advance(interval / 10);
        }

        throw new TimeoutException($"heartbeat {ordinal} never landed after advancing the fake clock well past its interval — the loop stopped repeating, or RunGradingHeartbeatLoopAsync is not sleeping on the TimeProvider it is handed");
    }

    /// <summary>Minimal <see cref="IRunRecordLogger"/> fake — every method a harmless no-op except <see cref="LogAsync"/>, which records each call and releases <see cref="Logged"/> once it has. The grading-heartbeat path touches ONLY LogAsync; every other member exists solely to satisfy the interface.</summary>
    private sealed class RecordingLogger : IRunRecordLogger
    {
        public List<(Guid RunId, string? NodeId, LogLevel Level, string Message)> Calls { get; } = new();

        /// <summary>Released AFTER each call is recorded, so a waiter that acquires it reads a <see cref="Calls"/> that already holds that heartbeat.</summary>
        public SemaphoreSlim Logged { get; } = new(0);

        public Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken cancellationToken)
        {
            Calls.Add((runId, nodeId, level, message));
            Logged.Release();
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
        public Task<Guid> NodeCompletedAsync(Guid runId, string nodeId, string iterationKey, IReadOnlyDictionary<string, JsonElement> outputs, IReadOnlyList<string>? routingHints, TimeSpan duration, CancellationToken cancellationToken) => Task.FromResult(Guid.Empty);
        public Task NodeFailedAsync(Guid runId, string nodeId, string iterationKey, string error, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AttemptFailedAsync(Guid runId, string nodeId, string iterationKey, int attempt, int maxAttempts, string error, TimeSpan duration, double retryInSeconds, Guid? parentRecordId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeSkippedAsync(Guid runId, string nodeId, string iterationKey, string reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeStorageUnavailableAsync(Guid runId, string nodeId, string iterationKey, string reason, string code, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NodeSuspendedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, DateTimeOffset? wakeAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IterationStartedAsync(Guid runId, string nodeId, int itemCount, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task IterationCompletedAsync(Guid runId, string nodeId, int itemCount, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<(Guid RecordId, Guid CorrelationId)> ExternalCallStartedAsync(Guid runId, string? nodeId, string target, string method, JsonElement? requestPayload, Guid? parentRecordId, CancellationToken cancellationToken) => Task.FromResult((Guid.NewGuid(), Guid.NewGuid()));
        public Task ExternalCallCompletedAsync(Guid runId, string? nodeId, Guid correlationId, int? statusCode, JsonElement? responsePayload, TimeSpan duration, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ExternalCallFailedAsync(Guid runId, string? nodeId, Guid correlationId, string target, string error, TimeSpan duration, string? category, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task WaitReissuedAsync(Guid runId, string nodeId, string iterationKey, string waitKind, Guid waitId, Guid byUserId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<Guid> RecordInteractionAsync(Guid runId, string recordType, string? nodeId, string iterationKey, Guid correlationId, Guid? parentRecordId, JsonElement payload, CancellationToken cancellationToken) => Task.FromResult(Guid.NewGuid());
    }
}
