using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Messages.Agents.Benchmark;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.UnitTests.Supervisor;

/// <summary>
/// 🟢 Unit: the P1.3 grading-heartbeat loop (<see cref="SupervisorTurnService.RunGradingHeartbeatLoopAsync"/>) —
/// pins the contract that keeps a long acceptance grade from looking abandoned to the reconciler WITHOUT waiting out
/// the real 90s production interval: a fake clock decides when each interval has elapsed, so the production value
/// itself costs nothing. The DB-observable effects (a real ledger row landing on its own context while the grade holds
/// the scope's, and the reconciler reading such a row as liveness) are proved at the integration tier — this pins the
/// loop mechanics: one pulse per elapsed interval and not a moment sooner, each on a DI scope of its own, a failed pulse
/// reported and survived rather than surfaced into the grade, and a quiet stop the instant it is cancelled.
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
    private const string Graded = "graded while every pulse failed";
    private static readonly TimeSpan Interval = SupervisorLane.AcceptanceGradeHeartbeatInterval;

    /// <summary>How far short of a full interval the "not yet" check stands: a loop sleeping even this much less than it was handed pulses inside that window.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    // The loop touches only the scope factory (each pulse's record logger) and the ILogger (a failed pulse's warning) —
    // every other dependency is stored by the ctor and never read on this path, so null! is safe here exactly as the
    // existing SupervisorTurnServiceTests pass null! for seams a path doesn't touch. The ctor's OWN record logger is a
    // recording fake rather than null!, so a pulse that wrote through it — the grade's shared context — is caught.
    private static SupervisorTurnService Service(PulseScopes pulses, RecordingLogger? injected = null, CapturingLogger? warnings = null) =>
        new(null!, null!, null!, db: Infrastructure.EmptyTestDb.New(), null!, null!, null!, null!, null!, injected ?? new RecordingLogger(), null!, null!, null!, new NullCompletionComposer(), null!, null!, pulses, warnings ?? (Microsoft.Extensions.Logging.ILogger<SupervisorTurnService>)NullLogger<SupervisorTurnService>.Instance);

    [Fact]
    public async Task The_loop_logs_once_per_elapsed_interval_while_uncancelled()
    {
        var time = new Infrastructure.HeartbeatClock();
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();

        var loop = Service(new PulseScopes(logger)).RunGradingHeartbeatLoopAsync(RunId, NodeId, Interval, cts.Token, time);

        // Three OBSERVED ticks prove it is a REPEATING loop, not a one-shot; the two advances around each one pin WHEN it
        // lands, not only that it did — nothing a step short of the interval, exactly one the moment it completes. A loop
        // that sleeps anything but the interval it was handed reds on one side or the other.
        for (var i = 1; i <= 3; i++)
        {
            await time.NextTimerAsync();

            time.Advance(Interval - Step);
            (await logger.Logged.WaitAsync(TimeSpan.FromMilliseconds(100))).ShouldBeFalse($"heartbeat {i} is owed only once a FULL interval of grading has passed — one {Step.TotalSeconds}s early means the loop sleeps less than it was handed");

            time.Advance(Step);
            (await logger.Logged.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue($"heartbeat {i} is owed the instant its interval completes");

            logger.Calls.Count.ShouldBe(i, $"exactly one heartbeat per elapsed interval — after {i} interval(s) there must be {i}");
        }

        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        logger.Calls.ShouldAllBe(c => c.RunId == RunId && c.NodeId == NodeId && c.Level == LogLevel.Info);
    }

    [Fact]
    public async Task Cancelling_stops_the_loop_without_throwing()
    {
        var time = new Infrastructure.HeartbeatClock();
        var logger = new RecordingLogger();
        using var cts = new CancellationTokenSource();

        var loop = Service(new PulseScopes(logger)).RunGradingHeartbeatLoopAsync(RunId, NodeId, Interval, cts.Token, time);

        // One tick first, so the cancel below lands MID-SLEEP on the next interval rather than before the loop ran.
        await time.NextTimerAsync();
        time.Advance(Interval);
        (await logger.Logged.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue("the first heartbeat is owed once its interval has passed");
        await time.NextTimerAsync();

        cts.Cancel();

        // Must complete cleanly — OperationCanceledException is caught INSIDE the loop, never surfaced to the caller
        // (the P1.3 call site's finally-block await must never itself need a try/catch for this). Recorded rather than
        // asserted with Should.NotThrowAsync, which passes a CANCELED task without a word and so could never catch the
        // very escape this test is named for. Bounded, so a loop that ignores the cancel mid-sleep fails, not hangs.
        var escaped = await Record.ExceptionAsync(() => loop.WaitAsync(TimeSpan.FromSeconds(10)));

        escaped.ShouldBeNull("a cancel mid-sleep must end the loop at once and quietly — a TimeoutException means it outlived the grade it protects, a TaskCanceledException that the cancel escaped to the caller");

        time.Advance(Interval * 3);
        (await logger.Logged.WaitAsync(TimeSpan.FromMilliseconds(200))).ShouldBeFalse("a cancelled loop logs no more heartbeats, however much time passes");
        logger.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task An_already_cancelled_token_produces_zero_heartbeats()
    {
        var logger = new RecordingLogger();
        var pulses = new PulseScopes(logger);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Service(pulses).RunGradingHeartbeatLoopAsync(RunId, NodeId, Interval, cts.Token, new Infrastructure.HeartbeatClock()).WaitAsync(TimeSpan.FromSeconds(10));

        logger.Calls.ShouldBeEmpty("a grade that finishes before the FIRST tick never needs a heartbeat");
        pulses.Created.ShouldBe(0, "no pulse, no scope");
    }

    /// <summary>
    /// Every call site awaits this loop in the <c>finally</c> around the grade it protects, so a loop that ends faulted
    /// REPLACES the grade with its error. The live shape was a pulse writing through the grade's own DbContext mid-query
    /// — EF's second-operation guard, an <see cref="InvalidOperationException"/> — but any failed write is the same shape.
    /// A failed pulse is a warning naming the run and node, the next pulse still fires, and the grade comes back graded.
    /// </summary>
    [Fact]
    public async Task A_failed_pulse_is_reported_and_never_replaces_the_grade()
    {
        var time = new Infrastructure.HeartbeatClock();
        var failing = new RecordingLogger(new InvalidOperationException("A second operation was started on this context instance before a previous operation completed."));
        var warnings = new CapturingLogger();
        using var heartbeatCts = new CancellationTokenSource();

        var heartbeat = Service(new PulseScopes(failing), warnings: warnings).RunGradingHeartbeatLoopAsync(RunId, NodeId, Interval, heartbeatCts.Token, time);

        BenchmarkGrade grade;
        try
        {
            for (var pulse = 1; pulse <= 3; pulse++)
            {
                await time.NextTimerAsync();
                time.Advance(Interval);

                (await failing.Logged.WaitAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue($"pulse {pulse} must still fire after {pulse - 1} failed");
            }

            grade = new BenchmarkGrade { Passed = true, Detail = Graded };
        }
        finally
        {
            // The call sites' own shape: whatever this await surfaces is what the grade becomes.
            heartbeatCts.Cancel();

            try { await heartbeat; }
            catch (OperationCanceledException) { }
        }

        grade.Detail.ShouldBe(Graded, "a failed pulse is a missed log line — it must never replace the grade it was keeping alive");
        heartbeat.IsCompletedSuccessfully.ShouldBeTrue("the loop ends only by cancellation, and quietly");

        warnings.Entries.Count.ShouldBe(3, "one warning per failed pulse — none swallowed silently, none doubled");
        warnings.Entries.ShouldAllBe(w => w.Level == Microsoft.Extensions.Logging.LogLevel.Warning && w.Exception is InvalidOperationException);
        warnings.Entries.ShouldAllBe(w => Equals(w.Properties["SupervisorRunId"], RunId) && Equals(w.Properties["NodeId"], NodeId), "a structured template naming the run and node, not an interpolated string");
    }

    /// <summary>
    /// The pulse runs concurrently with the grade, and the service's own scope holds the DbContext the grade is using, so
    /// each pulse writes through a scope of its OWN — one per pulse, disposed once the pulse returns — and never through the
    /// constructor-injected record logger, which shares that context. The collision itself is proved against real
    /// Postgres by <c>SupervisorGradingHeartbeatIsolationFlowTests</c>; this pins the wiring that avoids it.
    /// </summary>
    [Fact]
    public async Task Each_pulse_writes_through_a_record_logger_from_a_scope_of_its_own()
    {
        var time = new Infrastructure.HeartbeatClock();
        var scoped = new RecordingLogger();
        var injected = new RecordingLogger();
        var pulses = new PulseScopes(scoped);
        using var cts = new CancellationTokenSource();

        var loop = Service(pulses, injected).RunGradingHeartbeatLoopAsync(RunId, NodeId, Interval, cts.Token, time);

        await time.NextTimerAsync();

        for (var pulse = 1; pulse <= 3; pulse++)
        {
            time.Advance(Interval);

            // Re-armed ⇒ the pulse's write has returned and its scope's using block has closed.
            await time.NextTimerAsync();

            scoped.Calls.Count.ShouldBe(pulse, "every pulse writes through the record logger its own scope serves");
            pulses.Created.ShouldBe(pulse, "one fresh scope per pulse — not one per loop");
            pulses.Disposed.ShouldBe(pulse, "each pulse's scope is disposed as soon as its write returns");
        }

        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(10));

        injected.Calls.ShouldBeEmpty("a pulse never writes through the service's own record logger — that one shares the grade's DbContext");
    }

    /// <summary>
    /// The cadence production runs this heartbeat at, as a NUMBER. The fake clock above proves the loop honours whatever
    /// interval it is handed and says nothing about which one production hands it — the split <c>AgentRunLivenessTests</c>
    /// makes for the agent heartbeat. <see cref="SupervisorLane.AcceptanceGradeHeartbeatInterval"/>'s own doc calls the
    /// value pinned; nothing pinned it.
    /// </summary>
    [Fact]
    public void The_production_cadence_is_pinned_well_inside_the_liveness_window()
    {
        SupervisorLane.AcceptanceGradeHeartbeatInterval.ShouldBe(TimeSpan.FromSeconds(90));

        (StuckRunReconcilerService.LedgerLivenessWindow / SupervisorLane.AcceptanceGradeHeartbeatInterval).ShouldBeGreaterThanOrEqualTo(3, "a failed pulse is only a warning, so the liveness window must outlast a missed pulse or two — three pulses per window absorbs two in a row");
    }

    /// <summary>The scope factory a pulse opens its scope through: every scope serves <paramref name="logger"/> as its <see cref="IRunRecordLogger"/>, and the factory counts the scopes opened and disposed.</summary>
    private sealed class PulseScopes(IRunRecordLogger logger) : IServiceScopeFactory
    {
        private int _created;
        private int _disposed;

        public int Created => Volatile.Read(ref _created);
        public int Disposed => Volatile.Read(ref _disposed);

        public IServiceScope CreateScope()
        {
            Interlocked.Increment(ref _created);
            return new Scope(this, logger);
        }

        private sealed class Scope(PulseScopes owner, IRunRecordLogger logger) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => serviceType == typeof(IRunRecordLogger) ? logger : null;
            public void Dispose() => Interlocked.Increment(ref owner._disposed);
        }
    }

    /// <summary>Captures each entry's level, exception and structured properties — the named template values an interpolated message would not carry.</summary>
    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<SupervisorTurnService>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, Exception? Exception, IReadOnlyDictionary<string, object?> Properties)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception, (state as IEnumerable<KeyValuePair<string, object?>> ?? []).ToDictionary(p => p.Key, p => p.Value)));
    }

    /// <summary>Minimal <see cref="IRunRecordLogger"/> fake — every method a harmless no-op except <see cref="LogAsync"/>, which records each call, releases <see cref="Logged"/> once it has, and then fails with <paramref name="fault"/> when one is given (a pulse whose write failed). The grading-heartbeat path touches ONLY LogAsync; every other member exists solely to satisfy the interface.</summary>
    private sealed class RecordingLogger(Exception? fault = null) : IRunRecordLogger
    {
        public List<(Guid RunId, string? NodeId, LogLevel Level, string Message)> Calls { get; } = new();

        /// <summary>Released AFTER each call is recorded, so a waiter that acquires it reads a <see cref="Calls"/> that already holds that heartbeat.</summary>
        public SemaphoreSlim Logged { get; } = new(0);

        public Task LogAsync(Guid runId, string? nodeId, LogLevel level, string message, CancellationToken cancellationToken)
        {
            Calls.Add((runId, nodeId, level, message));
            Logged.Release();
            return fault is null ? Task.CompletedTask : Task.FromException(fault);
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
