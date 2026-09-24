using System.Diagnostics;
using System.Reflection;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Exceptions;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The two database writes the executor makes on every output poll of a live agent — the event batch and the spool
/// offset — pinned through the executor's own writer and checkpoint with a fake database, a fake completeness plane and
/// a virtual clock.
///
/// <para>The contract: a TRANSIENT fault (a reset, a pool timeout, 57P01) is ridden out — the same batch, with the same
/// row ids, is offered again until it lands — so nothing is lost and nothing reaches the run's verdict; past the bound
/// the batch is given up as a capture gap and the observation goes on; any OTHER fault — a verdict about the write, a
/// lost fence — surfaces from the first attempt exactly as before. The fake database answers only the two writes a poll
/// may make, so a terminal write, or any other call, fails the test on its own.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class AgentRunExecutorObserverWriteTests
{
    private static readonly AgentRunOwnerToken Owner = new(Guid.NewGuid(), Guid.NewGuid(), 3);
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_transient_fault_on_one_attempt_offers_the_same_batch_again_and_loses_nothing()
    {
        var world = new World(appendFault: offer => offer == 1 ? EfWrapped(AdminShutdown()) : null);
        var writer = world.Executor.NewEventWriter(Owner, TeamId);
        await writer.BufferAsync(Event("one"), CancellationToken.None);
        await writer.BufferAsync(Event("two"), CancellationToken.None);

        var flush = writer.FlushAsync(CancellationToken.None);
        await AdvanceUntilAsync(world.Clock, flush, "the batch refused by one transient fault was never offered again");

        (await Record.ExceptionAsync(() => flush)).ShouldBeNull("a fault the retry outlived is not the observer's to raise — the tail loop goes on");
        world.Database.Offers.Count.ShouldBe(2, "one failed attempt, one that landed");
        world.Database.Offers[1].SequenceEqual(world.Database.Offers[0]).ShouldBeTrue("the retry offers the SAME batch — the same row ids — so an attempt that did commit is kept once by the append's id guard");
        world.Database.Landed.Select(pending => pending.Event.Text).ShouldBe(["one", "two"], "nothing the failed attempt carried is lost");
        world.Completeness.Gaps.ShouldBeEmpty("a batch that landed lost nothing, so there is no gap to name");

        await writer.FlushAsync(CancellationToken.None);

        world.Database.Offers.Count.ShouldBe(2, "a landed batch leaves the buffer, so the next flush has nothing to offer");
    }

    [Fact]
    public async Task A_database_that_never_answers_gives_the_batch_up_as_a_capture_gap_and_the_observation_goes_on()
    {
        var world = new World(appendFault: _ => AdminShutdown());
        var writer = world.Executor.NewEventWriter(Owner, TeamId);
        var outageBegan = world.Clock.GetUtcNow();
        await writer.BufferAsync(Event("lost-1"), CancellationToken.None);
        await writer.BufferAsync(Event("lost-2"), CancellationToken.None);

        var flush = writer.FlushAsync(CancellationToken.None);
        await AdvanceUntilAsync(world.Clock, flush, "the retry never gave the batch up, so a database outage now stalls the tail forever");

        (await Record.ExceptionAsync(() => flush)).ShouldBeNull("past the bound the observer degrades: an unanswered write is not the agent's verdict");
        world.Database.Offers.Count.ShouldBe(ObserverWriteRetry.MaxAttempts, "the batch is offered exactly as often as the bound allows");
        world.Database.Offers.ShouldAllBe(offer => offer.SequenceEqual(world.Database.Offers[0]), "every attempt offers the same batch");

        var gap = world.Completeness.Gaps.ShouldHaveSingleItem("the given-up batch must be named, or the completeness plane reports an event log this run does not have");
        (gap.TeamId, gap.AgentRunId, gap.SubjectKind).ShouldBe((TeamId, (Guid?)Owner.RunId, WorkflowRunDataOwnerKinds.SemanticEvent));
        (gap.Reason, gap.RangeKind, gap.Resolution).ShouldBe((CaptureGapReason.RemoteUnavailable, CaptureGapRangeKind.Time, CaptureGapResolution.Open), "the database never answered — an outage, not a refusal — and the window is all capture knows");
        gap.RangeStartedAt.ShouldBe(outageBegan, "the window opens at the first refused attempt");
        gap.RangeEndedAt.ShouldNotBeNull().ShouldBeGreaterThan(outageBegan);
        gap.ReasonDetail.ShouldNotBeNull().ShouldContain("2 normalized event(s)");
        world.Logger.Warnings.Count(message => message.Contains("given up")).ShouldBe(1, "one structured warning per batch given up");

        world.Database.Heal();
        await writer.BufferAsync(Event("after"), CancellationToken.None);
        await writer.FlushAsync(CancellationToken.None);

        world.Database.Offers[^1].Select(pending => pending.Event.Text).ShouldBe(["after"], "the given-up batch left the buffer — it is the gap's now, never re-offered beside newer events");
        world.Database.Landed.Select(pending => pending.Event.Text).ShouldBe(["after"]);
    }

    public enum Verdict
    {
        UniqueViolation,
        SaveChangesOverUniqueViolation,
        InjectedInvalidOperation,
        LostFence,
    }

    [Theory]
    [InlineData(Verdict.UniqueViolation)]
    [InlineData(Verdict.SaveChangesOverUniqueViolation)]
    [InlineData(Verdict.InjectedInvalidOperation)]
    [InlineData(Verdict.LostFence)]   // the tear-down arms (#2019) own a lost fence, and must hear of it at once
    public async Task A_fault_that_is_a_verdict_surfaces_from_the_first_attempt_and_is_never_offered_again(Verdict shape)
    {
        var verdict = VerdictOf(shape);
        var world = new World(appendFault: _ => verdict);
        var writer = world.Executor.NewEventWriter(Owner, TeamId);
        await writer.BufferAsync(Event("one"), CancellationToken.None);

        var flush = writer.FlushAsync(CancellationToken.None);
        await AdvanceUntilAsync(world.Clock, flush, $"a {shape} was waited on as though the database might still take the write");
        var fault = await Record.ExceptionAsync(() => flush);

        fault.ShouldBeSameAs(verdict, $"a {shape} surfaces unchanged, exactly as it did before the retry existed");
        world.Database.Offers.Count.ShouldBe(1, $"a {shape} would fail identically on every attempt, so it is never offered again");
        world.Completeness.Gaps.ShouldBeEmpty("a surfaced fault is the run's own failure path to name, not a gap");
    }

    [Fact]
    public async Task A_cancelled_observer_unwinds_mid_retry_as_the_cancellation_and_names_no_gap()
    {
        var world = new World(appendFault: _ => AdminShutdown());
        var writer = world.Executor.NewEventWriter(Owner, TeamId);
        using var observer = new CancellationTokenSource();
        await writer.BufferAsync(Event("one"), CancellationToken.None);

        var flush = writer.FlushAsync(observer.Token);
        await WaitAsync(() => world.Database.Offers.Count == 1, "the first attempt was never made");
        observer.Cancel();

        (await Record.ExceptionAsync(() => flush)).ShouldBeAssignableTo<OperationCanceledException>("a torn-down worker's tear-down arm decides what happens next, so the wait between attempts must unwind as the cancellation");
        world.Completeness.Gaps.ShouldBeEmpty("a tear-down is not a database outage, and the batch is the re-attach's to deliver");
    }

    [Fact]
    public async Task The_spool_offset_checkpoint_rides_out_a_transient_fault_with_the_same_handle()
    {
        var world = new World(handleFault: offer => offer == 1 ? AdminShutdown() : null);
        var checkpointed = Handle(offset: 4096);

        var checkpoint = world.Executor.CheckpointOffsetAsync(Owner, checkpointed, CancellationToken.None);
        await AdvanceUntilAsync(world.Clock, checkpoint, "the offset refused by one transient fault was never written again");

        (await Record.ExceptionAsync(() => checkpoint)).ShouldBeNull();
        world.Database.Handles.Count.ShouldBe(2);
        world.Database.Handles[1].ShouldBe(world.Database.Handles[0], "the retry writes the same checkpoint");
        world.Database.Handles[1].ShouldContain("4096");
    }

    [Fact]
    public async Task A_spool_offset_the_database_never_takes_is_skipped_rather_than_failing_the_run()
    {
        var world = new World(handleFault: _ => AdminShutdown());

        var checkpoint = world.Executor.CheckpointOffsetAsync(Owner, Handle(offset: 4096), CancellationToken.None);
        await AdvanceUntilAsync(world.Clock, checkpoint, "the offset retry never gave up");

        (await Record.ExceptionAsync(() => checkpoint)).ShouldBeNull("the events the offset covers are already durable; a lagging offset only costs a re-attach some re-delivery");
        world.Database.Handles.Count.ShouldBe(ObserverWriteRetry.MaxAttempts);
        world.Completeness.Gaps.ShouldBeEmpty("nothing was lost, so there is nothing to name");
        world.Logger.Warnings.Count(message => message.Contains("could not be checkpointed")).ShouldBe(1);
    }

    [Fact]
    public async Task A_lost_fence_on_the_offset_checkpoint_surfaces_at_once()
    {
        var fence = new AgentRunOwnershipLostException(Owner.RunId);
        var world = new World(handleFault: _ => fence);

        var checkpoint = world.Executor.CheckpointOffsetAsync(Owner, Handle(offset: 4096), CancellationToken.None);
        await AdvanceUntilAsync(world.Clock, checkpoint, "a lost fence was waited on as though the database might still take the write");
        var fault = await Record.ExceptionAsync(() => checkpoint);

        fault.ShouldBeSameAs(fence);
        world.Database.Handles.Count.ShouldBe(1);
    }

    [Fact]
    public void The_committed_retry_bound_is_pinned_and_a_full_schedule_fits_inside_its_budget()
    {
        // A rename or a quiet retune changes how long every running agent's tail may stall, and how much a blip costs
        // before it becomes a gap — pinned so either is a visible decision.
        ObserverWriteRetry.MaxAttempts.ShouldBe(8);
        ObserverWriteRetry.BudgetMilliseconds.ShouldBe(30_000);

        var schedule = Enumerable.Range(1, ObserverWriteRetry.MaxAttempts - 1).Select(ObserverWriteRetry.BackoffAfter).ToList();

        schedule.Select(wait => wait.TotalMilliseconds).ShouldBe([250, 500, 1_000, 2_000, 4_000, 4_000, 4_000]);
        schedule.Aggregate(TimeSpan.Zero, (sum, wait) => sum + wait).ShouldBeLessThan(TimeSpan.FromMilliseconds(ObserverWriteRetry.BudgetMilliseconds), "attempts that fail fast run out of COUNT; only attempts that hang run out of budget");
        ObserverWriteRetry.BackoffAfter(1_000).ShouldBe(TimeSpan.FromSeconds(4), "the ceiling holds for any attempt count");
    }

    [Fact]
    public async Task Attempts_that_hang_run_out_of_budget_before_they_run_out_of_count()
    {
        var world = new World(appendFault: _ => AdminShutdown(), appendHangs: TimeSpan.FromSeconds(10));
        var writer = world.Executor.NewEventWriter(Owner, TeamId);
        await writer.BufferAsync(Event("one"), CancellationToken.None);

        var flush = writer.FlushAsync(CancellationToken.None);
        await AdvanceUntilAsync(world.Clock, flush, "a write whose attempts each hang never gave up");

        (await Record.ExceptionAsync(() => flush)).ShouldBeNull();
        world.Database.Offers.Count.ShouldBe(3, "10s + 0.25s + 10s + 0.5s + 10s leaves no room for a fourth attempt inside the 30s budget");
        world.Completeness.Gaps.ShouldHaveSingleItem();
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static AgentEvent Event(string text) => new() { Kind = AgentEventKind.AssistantMessage, Text = text };

    private static SandboxHandle Handle(long offset) => new() { Kind = "local", ProcessId = 4242, SpoolDirectory = "/spool/observer-write", Deadline = DateTimeOffset.UnixEpoch.AddHours(1), StdoutOffset = offset };

    private static PostgresException AdminShutdown() => new("terminating connection due to administrator command", "FATAL", "FATAL", PostgresErrorCodes.AdminShutdown);

    /// <summary>How an EF query — the ownership check every append starts with — surfaces a transient fault: the Npgsql provider's execution strategy wraps it.</summary>
    private static InvalidOperationException EfWrapped(Exception inner) => new("An exception has been raised that is likely due to a transient failure.", inner);

    private static Exception VerdictOf(Verdict shape) => shape switch
    {
        Verdict.UniqueViolation => new PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation),
        Verdict.SaveChangesOverUniqueViolation => new DbUpdateException("An error occurred while saving the entity changes.", new PostgresException("duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation)),
        Verdict.InjectedInvalidOperation => new InvalidOperationException("injected fault: simulated DB failure flushing a batched agent-event write"),
        Verdict.LostFence => new AgentRunOwnershipLostException(Owner.RunId),
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>
    /// Push the virtual clock forward in small steps until <paramref name="work"/> settles. Stepped rather than jumped:
    /// a jump can land before the next wait's timer is armed, which leaves the timer due after the jump and the test
    /// waiting on a moment that never comes.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, Task work, string signal)
    {
        var watch = Stopwatch.StartNew();

        while (!work.IsCompleted)
        {
            if (watch.Elapsed > Patience) throw new Xunit.Sdk.XunitException($"{signal} (advanced the virtual clock to {clock.GetUtcNow():O} over {Patience.TotalSeconds:F0}s of real time)");

            clock.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(1);
        }
    }

    private static async Task WaitAsync(Func<bool> condition, string signal)
    {
        var watch = Stopwatch.StartNew();

        while (!condition())
        {
            if (watch.Elapsed > Patience) throw new Xunit.Sdk.XunitException($"{signal} (waited {Patience.TotalSeconds:F0}s)");

            await Task.Delay(5);
        }
    }

    private sealed class World
    {
        public World(Func<int, Exception?>? appendFault = null, Func<int, Exception?>? handleFault = null, TimeSpan appendHangs = default)
        {
            var runs = DispatchProxy.Create<IAgentRunService, ObserverDatabase>();
            Database = (ObserverDatabase)(object)runs;
            Database.Clock = Clock;
            Database.AppendFault = appendFault;
            Database.HandleFault = handleFault;
            Database.AppendHangs = appendHangs;

            Executor = new AgentRunExecutor(runs, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, Logger, completeness: Completeness, clock: Clock);
        }

        public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UnixEpoch);
        public ObserverDatabase Database { get; }
        public RecordingCompleteness Completeness { get; } = new();
        public CapturingLogger Logger { get; } = new();
        public AgentRunExecutor Executor { get; }
    }

    /// <summary>The run service reduced to the two writes one output poll may make — the owner's event batch and its spool offset. Any other call fails the test, a terminal write above all.</summary>
    public class ObserverDatabase : DispatchProxy
    {
        public FakeTimeProvider Clock { get; set; } = null!;
        public Func<int, Exception?>? AppendFault { get; set; }
        public Func<int, Exception?>? HandleFault { get; set; }
        public TimeSpan AppendHangs { get; set; }

        /// <summary>Every batch offered, in order — a copy, so a later change to the writer's buffer cannot rewrite history.</summary>
        public List<IReadOnlyList<PendingAgentEvent>> Offers { get; } = [];

        /// <summary>What the database kept.</summary>
        public List<PendingAgentEvent> Landed { get; } = [];

        public List<string> Handles { get; } = [];

        public void Heal()
        {
            AppendFault = null;
            HandleFault = null;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAgentRunService.AppendEventsAsync) && args![0] is AgentRunOwnerToken owner) return Append(owner, (IReadOnlyList<PendingAgentEvent>)args[1]!);
            if (method.Name == nameof(IAgentRunService.SetRunnerHandleAsync) && args![0] is AgentRunOwnerToken fenced) return SetHandle(fenced, (string)args[1]!);

            throw new NotSupportedException($"an output poll may only append its batch and checkpoint its offset — it called {method.Name}");
        }

        private Task Append(AgentRunOwnerToken owner, IReadOnlyList<PendingAgentEvent> batch)
        {
            owner.ShouldBe(Owner, "every write is fenced to the pass's own owner token");
            Offers.Add(batch.ToList());

            if (AppendHangs > TimeSpan.Zero) Clock.Advance(AppendHangs);

            if (AppendFault?.Invoke(Offers.Count) is { } fault) return Task.FromException(fault);

            Landed.AddRange(batch);
            return Task.CompletedTask;
        }

        private Task SetHandle(AgentRunOwnerToken owner, string handleJson)
        {
            owner.ShouldBe(Owner);
            Handles.Add(handleJson);

            return HandleFault?.Invoke(Handles.Count) is { } fault ? Task.FromException(fault) : Task.CompletedTask;
        }
    }

    public sealed class RecordingCompleteness : IRunDataCompletenessWriter
    {
        public List<WorkflowRunCaptureGap> Gaps { get; } = [];

        public Task<bool> NoticeAsync(WorkflowRunCaptureGap gap, CancellationToken cancellationToken)
        {
            Gaps.Add(gap);
            return Task.FromResult(true);
        }

        public Task<bool> InitializeAsync(RunDataManifestInitialization initialization, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> AdvanceAsync(RunDataFacetAdvance advance, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> UnstateExpectationAsync(Guid teamId, Guid workflowRunId, string facet, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    public sealed class CapturingLogger : ILogger<AgentRunExecutor>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings { get { lock (_warnings) return _warnings.ToList(); } }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning) return;

            lock (_warnings) _warnings.Add(formatter(state, exception));
        }
    }
}
