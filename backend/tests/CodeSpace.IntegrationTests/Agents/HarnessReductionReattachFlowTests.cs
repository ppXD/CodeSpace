using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Reduction;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The G1 spine, end to end (Rule 12 high fidelity): a REAL detached supervisor, the real executor on both its live
/// and its re-attach path, the real plane and real Postgres — because every invariant this slice adds is a database
/// one. What it proves is the defect that made a warm resume cold: a worker replaced mid-run used to fold only the
/// tail it could still see, so the recovered state was a different value nothing downstream could tell apart from the
/// right one.
///
/// <para>The run's own outcome is asserted UNCHANGED against a plane that refuses to write a checkpoint and one whose
/// stored reduction cannot be resumed. That is not politeness: this plane is optional, and a guarantee that only
/// holds where it happens to be deployed is a guarantee that fails open.</para>
///
/// <para><b>These are the invariants only this tier can execute, named so their absence from a local unit run is not
/// mistaken for coverage.</b> That 0140's three-way count agreement ACCEPTS the checkpoints this pump writes; that
/// 0137's terminal arms accept the close ordering (attempts, then the execution, as two separate un-transacted
/// statements); that the run's fence, carried as a predicate on both of those statements, refuses a superseded worker
/// and still admits the one that reclaimed the run; that the resumed cursor is scoped to the attempt and the channel;
/// and that a re-delivered source line lands one row and not two. Only the last of those has a unit-tier shadow, over
/// a hand-driven pump — none of the database arms can be exercised without a real PostgreSQL, and the
/// <c>Backend · Integration</c> lane is where they run.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HarnessReductionReattachFlowTests
{
    private const string SixSteps = "for i in 1 2 3 4 5 6; do echo step$i; sleep 0.4; done";

    /// <summary>The session <see cref="SteppingHarness"/> states in its own structured frame — a canonical UUID, as both real harnesses emit.</summary>
    private static readonly Guid ScriptedSession = Guid.Parse("6b1f0c74-6b0e-4a0b-8d3a-9d2e5f7c1a42");

    /// <summary>The same six steps, preceded by the frame the harness reads its session out of — stated ONCE, at the start, which is the position a tail-only fold cannot recover from.</summary>
    private static string SessionThenSixSteps => $"echo '{SteppingHarness.SessionFrame}'; {SixSteps}";

    /// <summary>A run whose diagnostics and exit code are both fixed, so two runs of it differ only in whether the plane was deployed.</summary>
    private const string StepThenDiagnosticsThenFail = "printf 'step1\\n'; printf 'boom one\\nboom two\\n' >&2; exit 3";

    private readonly PostgresFixture _fixture;

    public HarnessReductionReattachFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The headline. A worker is torn down mid-stream, another re-attaches to the SAME live process, and the reduction
    /// it lands on is the one a fold of every recorded frame produces — prefix digest included, which is the witness
    /// that it reduced this exact prefix rather than a shorter one that merely counts consistently. The pre-restart
    /// checkpoint is asserted to be strictly shorter, so the test is about a resume and not about a run that happened
    /// to record everything after the seam.
    /// </summary>
    [Fact]
    public async Task A_re_attached_run_lands_on_the_reduction_of_its_whole_recorded_stream()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await TearDownMidStreamAsync(runId);

        var beforeRestart = await ReadCheckpointAsync(runId);
        beforeRestart.ShouldNotBeNull("the torn-down worker must have left a durable reduction behind, or there is nothing for the re-attach to resume from");

        var recordsBeforeRestart = await CountRecordsAsync(runId);
        beforeRestart.RecordsConsumed.ShouldBe(recordsBeforeRestart,
            customMessage: "the stored position must claim exactly the frames that are durable — a position ahead of them is the failure 0140 refuses, and one behind them is a prefix silently shortened");

        await ReclaimAsync(runId);
        await ReattachAsync(runId);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded,
            customMessage: "the re-attached observer tailed the live process to completion");

        (await scope.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(candidate => candidate.AgentRunId == runId)).State
            .ShouldBe(HarnessExecutionState.Exited,
                customMessage: "the worker that RECLAIMED this run holds its current fence, so the fence on terminalization must let it close the execution — refusing here would trade a superseded worker's false close for a live one's blocked generation");

        var frames = await RecordedStreamAsync(runId);
        frames.Count.ShouldBeGreaterThan(recordsBeforeRestart,
            customMessage: "the re-attach must have recorded frames of its own, or this proves nothing about a resume");

        var afterRestart = await ReadCheckpointAsync(runId);
        afterRestart!.RecordsConsumed.ShouldBe(frames.Count);

        var reduced = Reduced(afterRestart);
        reduced.ShouldBe(WholeStreamFold(afterRestart.ExecutionId, frames),
            customMessage: "a resumed reduction that is not the whole-stream fold is exactly today's defect: the tail folded into a state nobody can tell apart from the right one");
        reduced.PrefixDigest.ShouldNotBe(WholeStreamFold(afterRestart.ExecutionId, TailOf(frames, recordsBeforeRestart)).PrefixDigest,
            customMessage: "and the digest of the tail ALONE must differ, or the assertion above would pass for a fold that recovered nothing");
    }

    /// <summary>
    /// The headline this lane exists for: a NAMED fact survives a worker replacement, end to end. Until the grounded
    /// projector landed, every projection in production was <c>Derived</c> and the fold takes a named fact only from an
    /// exactly grounded one — so a re-attach recovered the counts, the channel set and the prefix digest, and nothing
    /// that named anything.
    ///
    /// <para>The falsifier is the last assertion: the fold of the tail ALONE must not know the session. The harness
    /// states it once, in the first frame, before the worker is torn down — so a resume that recovered nothing would
    /// answer null and this test would fail rather than pass on a run that happened to restate it.</para>
    /// </summary>
    [Fact]
    public async Task A_session_stated_before_a_worker_replacement_survives_the_re_attach()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await TearDownMidStreamAsync(runId, SessionThenSixSteps);

        var beforeRestart = await ReadCheckpointAsync(runId);
        Reduced(beforeRestart.ShouldNotBeNull()).FirstSessionId.ShouldBe(ScriptedSession,
            customMessage: "the live worker must have recorded and folded the session frame before it was torn down, or there is no fact for the re-attach to recover and the rest of this test is about nothing");

        var recordsBeforeRestart = await CountRecordsAsync(runId);

        await ReclaimAsync(runId);
        await ReattachAsync(runId);

        var afterRestart = await ReadCheckpointAsync(runId);
        Reduced(afterRestart.ShouldNotBeNull()).FirstSessionId.ShouldBe(ScriptedSession,
            customMessage: "the replacement worker never saw the frame that named this session; recovering it from the stored reduction is the whole point of the checkpoint");

        var projections = await ProjectionsAsync(runId);
        var grounded = projections.Where(projection => projection.ProjectionQuality is SemanticProjectionQuality.Exact or SemanticProjectionQuality.RedactedExact).ToList();

        grounded.ShouldHaveSingleItem().SessionId.ShouldBe(ScriptedSession,
            customMessage: "exactly one frame WAS the harness's session record, and only a projection of that frame may claim the harness's own words");
        grounded[0].SourceNativeRecordIds.Length.ShouldBe(1,
            customMessage: "the database refuses an exact claim that cites no frame, and refuses one over a frame that was masked or never captured");
        projections.Count(projection => projection.ProjectionQuality == SemanticProjectionQuality.Derived).ShouldBeGreaterThan(0,
            customMessage: "the normalized projections must still be Derived — promoting them is exactly the laundering the quality vocabulary exists to prevent");

        var frames = await RecordedStreamAsync(runId);
        WholeStreamFold(afterRestart.ExecutionId, TailOf(frames, recordsBeforeRestart)).FirstSessionId.ShouldBeNull(
            customMessage: "if the tail alone knew the session id, this test would pass over a fold that recovered nothing");
    }

    /// <summary>
    /// Stderr, which reached no native record at all: it was read whole into memory on every terminal path and then
    /// dropped by the executor's mapping, so a harness's own diagnostics died with the spool. They now land as records
    /// of their own channel, with their own contiguous geometry — and NOT as semantic events, because a parser written
    /// for the harness's stdout protocol would read a diagnostic as an event nobody emitted.
    ///
    /// <para>The run's own outcome is asserted against the SAME run with no plane at all, error text included. That is
    /// the constraint, not politeness: this plane is optional, and a diagnostics path that changed an error message
    /// would have made a shadow plane load bearing.</para>
    /// </summary>
    [Fact]
    public async Task A_runs_diagnostics_become_records_of_their_own_channel_and_leave_its_outcome_untouched()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var withPlane = await CreateScriptedRunAsync(teamId);
        var bare = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(withPlane, new SteppingHarness(StepThenDiagnosticsThenFail));
        await ExecuteAsync(bare, new SteppingHarness(StepThenDiagnosticsThenFail), plane: _ => null);

        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();

        var observed = await runs.GetAsync(withPlane, CancellationToken.None);
        var expected = await runs.GetAsync(bare, CancellationToken.None);

        observed.Status.ShouldBe(expected.Status);
        observed.Error.ShouldBe(expected.Error,
            customMessage: "the run's error text must be byte-identical to the one a run with no plane produces — routing stderr through capture may not change a single character of what the run resolves to");

        var records = await OrderedRecordsAsync(withPlane);
        var diagnostics = records.Where(record => record.Channel == NativeRecordChannel.Stderr).ToList();

        diagnostics.Select(record => record.InlinePayload).ShouldBe(new[] { "boom one", "boom two" },
            customMessage: "the harness's own diagnostics are what this makes durable; recording none of them leaves them dying with the spool exactly as before");
        diagnostics.Select(record => record.Normalization).ShouldAllBe(normalization => normalization == NativeRecordNormalization.NotParsed,
            customMessage: "Unrecognized would assert a parse that never ran, and would fill 'which frames could we not interpret' with every diagnostic line the run wrote");
        diagnostics.Select(record => record.Ordinal).ShouldBe(new long[] { 0, 1 });
        diagnostics.Select(record => record.SourceOffsetBytes).ShouldBe(new long[] { 0, 9 },
            customMessage: "stderr carries its own contiguous source geometry, counted from its own start — 'boom one' plus the terminator the stream carried");
        diagnostics.Select(record => record.StreamId).Distinct().Count().ShouldBe(1,
            customMessage: "one opening is one stream; sharing stdout's would collide two channels' ordinals in a sequence 0139 requires contiguous");

        records.Select(record => record.AttemptId).Distinct().Count().ShouldBe(1,
            customMessage: "the diagnostics belong to the process the stdout opening already recorded — a second attempt row would claim this run launched two processes");

        Reduced((await ReadCheckpointAsync(withPlane)).ShouldNotBeNull()).ChannelsSeen.ShouldContain(NativeRecordChannel.Stderr,
            customMessage: "the diagnostics ride the same reduction as the frames beside them, or the stored prefix witnesses a stream the process never produced");
    }

    /// <summary>
    /// The seam, under the re-delivery it actually has. A frame becomes durable at its batch write while the spool
    /// offset that covers it is persisted afterwards, so the records legitimately run AHEAD of that offset and a
    /// re-attach is handed those lines a second time. This test stages that window at its widest — the offset rewound
    /// to zero, so the WHOLE recorded prefix is re-delivered — and asserts the property the geometry alone cannot give:
    /// each source line has exactly one record for the attempt. Recording it twice is not an untidy duplicate, it is a
    /// reduction that counts a line twice and chains a digest witnessing a prefix the process never emitted.
    ///
    /// <para>The arithmetic this replaces could not fail: every stream's offsets are contiguous BY CONSTRUCTION for any
    /// cursor the plane hands back, duplicate or not, so it held exactly as well over a double-counted stream.</para>
    /// </summary>
    [Fact]
    public async Task A_re_attach_records_each_re_delivered_source_line_exactly_once()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await TearDownMidStreamAsync(runId);

        // The crash that lands between a batch's write and the offset persist, staged rather than raced for: the
        // records stay where the torn-down worker left them and the resume position goes back to the start.
        await RewindResumeOffsetAsync(runId);

        var head = await RecordedHeadAsync(runId);
        head.ShouldBeGreaterThan(0,
            customMessage: "nothing was recorded before the tear-down, so no line can be re-delivered and this test would prove nothing — check that the live path captured at least one checkpoint's frames");

        await ReclaimAsync(runId);
        await ReattachAsync(runId);

        var records = await OrderedRecordsAsync(runId);
        records.Select(record => record.StreamId).Distinct().Count().ShouldBe(2,
            customMessage: "the re-attach opens a stream of its own, so a seam actually exists to be tested");

        records.GroupBy(record => (record.AttemptId, record.Channel, record.Digest)).Where(group => group.Count() > 1).ShouldBeEmpty(
            customMessage: "a source line the re-attach was handed again is already a row; recording it a second time double-counts it in the reduction and chains its digest twice into a prefix the process never produced");

        records.First(record => record.StreamId == records[^1].StreamId).SourceOffsetBytes.ShouldBe(head,
            customMessage: "the resumed stream's first OWN frame starts exactly where the recorded ones stop — below that it is re-delivered and above it there would be a hole");
    }

    /// <summary>
    /// The write authority the terminalize path used to be missing. A superseded worker reaches terminalization on
    /// exactly the reclaim-for-reattach path: a lost completion CAS raises the same
    /// <c>AgentRunTransitionException</c> the already-terminal branch swallows, and it then falls through to close the
    /// execution. Unfenced, it stamps a LIVE attempt Lost and closes a live execution — into rows 0137 makes immutable,
    /// while the worker that reclaimed the run is still observing that very process.
    /// </summary>
    [Fact]
    public async Task A_superseded_worker_cannot_close_the_execution_a_reclaim_took_from_it()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await TearDownMidStreamAsync(runId);

        var superseded = await FenceEpochAsync(runId);
        await ReclaimAsync(runId);

        using var scope = _fixture.BeginScope();
        var executions = (INativeRecordExecutionPlane)scope.Resolve<INativeRecordPlane>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        await executions.TerminalizeAsync(teamId, runId, superseded, CancellationToken.None);

        (await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(candidate => candidate.AgentRunId == runId)).State
            .ShouldBe(HarnessProcessAttemptState.Running,
                customMessage: "the process this attempt names is alive and the worker that reclaimed the run is observing it; Lost is immutable, so writing it here makes the real outcome unrecordable forever");
        (await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(candidate => candidate.AgentRunId == runId)).State
            .ShouldBe(HarnessExecutionState.Running,
                customMessage: "and closing the execution would leave the re-attach with no live process to resume, disabling the recovery this whole slice exists to provide");

        await executions.TerminalizeAsync(teamId, runId, await FenceEpochAsync(runId), CancellationToken.None);

        (await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(candidate => candidate.AgentRunId == runId)).State
            .ShouldBe(HarnessExecutionState.Exited,
                customMessage: "the holder of the CURRENT fence must still be able to close it, or the predicate has replaced one broken outcome with another");
    }

    /// <summary>
    /// Every executor terminal releases the execution. Until this slice nothing did, so a completed run left a Running
    /// row behind — which is not untidy but blocking: 0137's generation gate refuses to open a generation over a live
    /// predecessor, so the Agent Run's next execution would be unrepresentable.
    /// </summary>
    [Fact]
    public async Task A_completed_run_leaves_its_harness_execution_terminal()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new SteppingHarness("printf 'step1\\n'"));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var execution = await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(candidate => candidate.AgentRunId == runId);
        execution.State.ShouldBe(HarnessExecutionState.Exited);
        execution.TerminalAt.ShouldNotBeNull();
        execution.LeaseOwnerId.ShouldBeNull("a terminal execution releases its lease, and this one never took it");

        (await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().CountAsync(candidate => candidate.AgentRunId == runId && candidate.State == HarnessProcessAttemptState.Running))
            .ShouldBe(0, customMessage: "an execution cannot be terminal over a live attempt — 0137 refuses it, so leaving one open would have silently failed the close");
    }

    /// <summary>
    /// The forced terminal. A parser that throws takes the round down before the pump ever records how the process
    /// ended, so its attempt is left open — and the executor's terminal must still release it, with the reason that
    /// says the outcome was never observed rather than an exit nobody saw.
    /// </summary>
    [Fact]
    public async Task A_run_that_failed_mid_round_still_releases_its_execution_with_an_unrecorded_outcome()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new SteppingHarness("printf 'step1\\nTHROW\\n'"));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Failed,
            customMessage: "the parser's throw resolves the run exactly as it did before any of this plane existed");

        var execution = await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(candidate => candidate.AgentRunId == runId);
        execution.State.ShouldBe(HarnessExecutionState.Exited);

        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(candidate => candidate.AgentRunId == runId);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Lost,
            customMessage: "nobody recorded this process's outcome, and an unknown outcome must never read as a clean one");
        attempt.ExitCode.ShouldBeNull();
        attempt.ErrorCode.ShouldBe(NativeRecordPlane.ProcessOutcomeUnrecordedErrorCode);
    }

    /// <summary>
    /// The non-load-bearing claim, asked of the two failures this slice adds. A checkpoint that will not write and a
    /// stored reduction that cannot be resumed must each leave the run resolving exactly as a run with no plane at all
    /// does — and the second must still capture its frames, because losing a reduction is not losing a record.
    /// </summary>
    [Fact]
    public async Task A_refused_checkpoint_and_an_unresumable_reduction_leave_the_run_unchanged()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var bare = await CreateScriptedRunAsync(teamId);
        var refusedCheckpoint = await CreateScriptedRunAsync(teamId);
        var unresumable = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(bare, new SteppingHarness("printf 'step1\\nstep2\\n'"), plane: _ => null);
        await ExecuteAsync(refusedCheckpoint, new SteppingHarness("printf 'step1\\nstep2\\n'"), plane: inner => new RefusingCheckpointPlane(inner));
        await ExecuteAsync(unresumable, new SteppingHarness("printf 'step1\\nstep2\\n'"), plane: inner => new UnresumableReductionPlane(inner));

        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var expected = await runs.GetAsync(bare, CancellationToken.None);

        foreach (var runId in new[] { refusedCheckpoint, unresumable })
        {
            var run = await runs.GetAsync(runId, CancellationToken.None);
            run.Status.ShouldBe(expected.Status, customMessage: "a reduction failure that changes what a run resolves to has made a shadow plane load bearing");
            run.Error.ShouldBe(expected.Error);

            (await runs.GetEventsAsync(runId, run.TeamId, 0, CancellationToken.None)).Select(candidate => candidate.Text)
                .ShouldBe(new[] { "step1", "step2" }, customMessage: "the normalized log is what it always was");
        }

        (await db.WorkflowRunHarnessReductionCheckpoint.AsNoTracking().CountAsync(candidate => candidate.AgentRunId == refusedCheckpoint))
            .ShouldBe(0, customMessage: "a checkpoint that could not be written must not be half-written");
        (await db.WorkflowRunNativeRecord.AsNoTracking().CountAsync(candidate => candidate.AgentRunId == unresumable))
            .ShouldBe(2, customMessage: "a reduction that cannot resume still leaves every frame captured — losing the fold of a frame is not losing the frame");
    }

    /// <summary>
    /// Rewinds the persisted resume position to the start of the spool while leaving the durable records exactly where
    /// the torn-down worker left them. That is the state a crash between a batch's write and the offset persist
    /// produces — records AHEAD of the position a re-attach resumes at — staged deterministically rather than raced
    /// for through a buffer-cap flush whose timing no test can pin, and at its widest so the whole recorded prefix is
    /// re-delivered.
    /// </summary>
    private async Task RewindResumeOffsetAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var handle = JsonSerializer.Deserialize<SandboxHandle>((await runs.GetAsync(runId, CancellationToken.None)).RunnerHandleJson!, AgentJson.Options)!;

        await runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle with { StdoutOffset = 0 }, AgentJson.Options), CancellationToken.None);
    }

    /// <summary>The first source position no record covers, computed as the plane computes it. One attempt on one channel in these runs, so the run-wide maximum IS that process's head.</summary>
    private async Task<long> RecordedHeadAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        var head = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNativeRecord.AsNoTracking()
            .Where(record => record.AgentRunId == runId)
            .MaxAsync(record => (long?)(record.SourceOffsetBytes + record.SourceLengthBytes));

        return head is null ? 0 : head.Value + 1;
    }

    private async Task<long> FenceEpochAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking()
            .Where(run => run.Id == runId).Select(run => run.FenceEpoch).SingleAsync();
    }

    /// <summary>Runs the live path until the harness has seen a step, then tears the worker down exactly as a pod shutdown does: the process keeps running, the run stays Running, and the observer is gone.</summary>
    private async Task TearDownMidStreamAsync(Guid runId, string script = SixSteps)
    {
        using var teardown = new CancellationTokenSource();

        await Should.ThrowAsync<OperationCanceledException>(() => ExecuteAsync(runId, new SteppingHarness(script, teardown, "step3"), teardown.Token));

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Running,
            customMessage: "a worker tear-down leaves the run for a re-attach; if it landed terminal the rest of this test is about something else");
    }

    private async Task ReclaimAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<IAgentRunService>().ReclaimForReattachAsync(runId, CancellationToken.None)).ShouldBeTrue();
    }

    private async Task ExecuteAsync(Guid runId, IAgentHarness harness, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(runId, harness, plane: inner => inner, cancellationToken);

    private async Task ExecuteAsync(Guid runId, IAgentHarness harness, Func<INativeRecordPlane, INativeRecordPlane?> plane, CancellationToken cancellationToken = default)
    {
        using var scope = _fixture.BeginScope();

        await Executor(scope, harness, plane).ExecuteAsync(runId, cancellationToken);
    }

    private async Task ReattachAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        await Executor(scope, new SteppingHarness(SixSteps), inner => inner).ReattachAsync(runId, CancellationToken.None);
    }

    private static AgentRunExecutor Executor(ILifetimeScope scope, IAgentHarness harness, Func<INativeRecordPlane, INativeRecordPlane?> plane)
    {
        var registry = new AgentHarnessRegistry(new[] { harness });

        return new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            registry,
            new HarnessModelReconciler(registry, scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactStore>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IArtifactManifestStore>(),
            scope.Resolve<ICaptureIntentService>(),
            scope.Resolve<IEnumerable<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>>(),
            NullLogger<AgentRunExecutor>.Instance,
            logCapture: null,
            nativeRecords: plane(scope.Resolve<INativeRecordPlane>()));
    }

    private async Task<WorkflowRunHarnessReductionCheckpoint?> ReadCheckpointAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunHarnessReductionCheckpoint.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.AgentRunId == runId);
    }

    private async Task<IReadOnlyList<WorkflowRunSemanticEvent>> ProjectionsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunSemanticEvent.AsNoTracking()
            .Where(candidate => candidate.AgentRunId == runId).ToListAsync();
    }

    private async Task<int> CountRecordsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNativeRecord.AsNoTracking().CountAsync(candidate => candidate.AgentRunId == runId);
    }

    /// <summary>
    /// Every recorded frame of the run, in the order the openings produced them. Ordering by ingestion and then by the
    /// stream's own ordinal, NOT by ordinal alone: a re-attach's stream is its own and starts again at zero, so an
    /// ordinal-only sort would interleave the two sides of the seam into an order no fold ever saw.
    /// </summary>
    private async Task<IReadOnlyList<WorkflowRunNativeRecord>> OrderedRecordsAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();

        var records = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunNativeRecord.AsNoTracking()
            .Where(candidate => candidate.AgentRunId == runId).ToListAsync();

        return records.OrderBy(record => record.IngestedAt).ThenBy(record => record.Ordinal).ToList();
    }

    /// <summary>The recorded stream read back as the frames a fold consumes, so a whole-stream fold can be computed independently of the one the run performed.</summary>
    private async Task<IReadOnlyList<HarnessReductionFrame>> RecordedStreamAsync(Guid runId)
    {
        var records = await OrderedRecordsAsync(runId);

        using var scope = _fixture.BeginScope();
        var projections = await scope.Resolve<CodeSpaceDbContext>().WorkflowRunSemanticEvent.AsNoTracking()
            .Where(candidate => candidate.AgentRunId == runId).ToListAsync();

        return records.Select(record => new HarnessReductionFrame
        {
            Record = Frame(record),
            Projections = projections.Where(projection => projection.SourceNativeRecordIds.Contains(record.Id)).Select(Projection).ToList(),
        }).ToList();
    }

    private static HarnessReducedStateV1 WholeStreamFold(Guid executionId, IReadOnlyList<HarnessReductionFrame> frames)
    {
        var fold = new HarnessReductionFold(HarnessReductionFold.SeedCheckpoint(executionId));

        foreach (var frame in frames) fold.Add(frame);

        return fold.Checkpoint.State;
    }

    /// <summary>The frames after the restart, renumbered onto a fresh frontier — what a fold that started from nothing would have had to consume, and the state this test proves the resumed one is NOT.</summary>
    private static IReadOnlyList<HarnessReductionFrame> TailOf(IReadOnlyList<HarnessReductionFrame> frames, int prefix) =>
        frames.Skip(prefix).Select((frame, index) => frame with { Record = frame.Record with { StreamId = TailStreamId, Ordinal = index } }).ToList();

    private static readonly Guid TailStreamId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private static HarnessReducedStateV1 Reduced(WorkflowRunHarnessReductionCheckpoint stored) =>
        JsonSerializer.Deserialize<HarnessReducedStateV1>(stored.ReducedStateJson, AgentJson.Options)!;

    private static NativeRecordV1 Frame(WorkflowRunNativeRecord record) => new()
    {
        ContractVersion = record.ContractVersion,
        RecordId = record.Id,
        StreamId = record.StreamId,
        Ordinal = record.Ordinal,
        Channel = record.Channel,
        NativeType = record.NativeType,
        NativeSchema = record.NativeSchema,
        NativeSchemaVersion = record.NativeSchemaVersion,
        OccurredAt = record.OccurredAt,
        IngestedAt = record.IngestedAt,
        ByteOffset = record.SourceOffsetBytes,
        ByteLength = record.SourceLengthBytes,
        InlinePayload = record.InlinePayload,
        DigestAlgorithm = record.DigestAlgorithm,
        Digest = record.Digest,
        SizeBytes = record.SizeBytes,
        Encoding = record.PayloadEncoding,
        Redaction = record.Redaction,
        IsFinal = record.IsFinal,
    };

    private static AgentSemanticEventV1 Projection(WorkflowRunSemanticEvent projection) => new()
    {
        ContractVersion = projection.ContractVersion,
        EventId = projection.Id,
        EventType = projection.EventType,
        EventSchemaVersion = projection.EventSchemaVersion,
        SourceNativeRecordIds = projection.SourceNativeRecordIds,
        ExecutionId = projection.ExecutionId,
        SessionId = projection.SessionId,
        TurnId = projection.TurnId,
        StepId = projection.StepId,
        ModelCallId = projection.ModelCallId,
        ToolCallId = projection.ToolCallId,
        CorrelationId = projection.CorrelationId,
        CausationId = projection.CausationId,
        Necessity = projection.Necessity,
        ProjectionQuality = projection.ProjectionQuality,
    };

    private async Task<Guid> CreateScriptedRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "reduction", Harness = "scripted", Model = "test-model", TimeoutSeconds = 1800 },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);

        return run.Id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"reduction-{userId:N}@test.local", Name = $"reduction-{userId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"reduction-{teamId:N}", Name = "Harness Reduction Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();

        return teamId;
    }

    /// <summary>
    /// A harness that echoes each step as one event and, when told to, tears its own worker down the moment a named
    /// step arrives — the pod shutdown, injected at a point the test can name rather than a sleep it has to guess.
    /// </summary>
    private sealed class SteppingHarness : IAgentHarness, IAgentGroundedFrameReader
    {
        private readonly string _script;
        private readonly CancellationTokenSource? _teardown;
        private readonly string? _tearDownAfter;

        public SteppingHarness(string script, CancellationTokenSource? teardown = null, string? tearDownAfter = null)
        {
            _script = script;
            _teardown = teardown;
            _tearDownAfter = tearDownAfter;
        }

        /// <summary>This harness's own structured session record — the frame whose CONTENT is the identity, which is what makes a fact read out of it exactly grounded rather than derived.</summary>
        internal static string SessionFrame => $"{{\"type\":\"session\",\"session_id\":\"{ScriptedSession:D}\"}}";

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        /// <summary>Answers for THAT frame and nothing else, exactly as the real readers do — a line that merely mentions the id grounds nothing.</summary>
        public GroundedSessionFrame? ReadSessionFrame(string nativeFrame) =>
            nativeFrame.Trim() == SessionFrame ? new GroundedSessionFrame { SessionId = ScriptedSession } : null;

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine)
        {
            var line = rawLine.Trim();

            if (line == "THROW") throw new InvalidOperationException("this native frame class is unreadable");
            if (string.IsNullOrWhiteSpace(line)) return Array.Empty<AgentEvent>();
            if (line == _tearDownAfter) _teardown?.Cancel();

            return new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = line } };
        }

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" });
    }

    /// <summary>The real plane with one thing broken: the write that carries a checkpoint. Frames and checkpoint share a transaction, so refusing it refuses the batch — which must cost the run nothing.</summary>
    private sealed class RefusingCheckpointPlane : INativeRecordPlane, INativeRecordReductionPlane
    {
        private readonly INativeRecordPlane _inner;

        public RefusingCheckpointPlane(INativeRecordPlane inner) => _inner = inner;

        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken) => _inner.OpenAsync(request, cancellationToken);

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken) => _inner.WriteAsync(batch, cancellationToken);

        public Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken) => _inner.CloseAsync(handle, exitCode, cancellationToken);

        public Task<HarnessReductionCheckpointV1?> ReadCheckpointAsync(Guid teamId, Guid executionId, string reducerKind, CancellationToken cancellationToken) =>
            ((INativeRecordReductionPlane)_inner).ReadCheckpointAsync(teamId, executionId, reducerKind, cancellationToken);

        public Task WriteReducedAsync(NativeRecordBatch batch, HarnessReductionCheckpointV1 checkpoint, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the reduction checkpoint could not be persisted");
    }

    /// <summary>The real plane handing back a checkpoint of a DIFFERENT reduction — a state shape behind the same field names, which the fold refuses to resume from rather than folding into something internally consistent and quietly wrong.</summary>
    private sealed class UnresumableReductionPlane : INativeRecordPlane, INativeRecordReductionPlane
    {
        private readonly INativeRecordPlane _inner;

        public UnresumableReductionPlane(INativeRecordPlane inner) => _inner = inner;

        public Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken) => _inner.OpenAsync(request, cancellationToken);

        public Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken) => _inner.WriteAsync(batch, cancellationToken);

        public Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken) => _inner.CloseAsync(handle, exitCode, cancellationToken);

        public Task<HarnessReductionCheckpointV1?> ReadCheckpointAsync(Guid teamId, Guid executionId, string reducerKind, CancellationToken cancellationToken) =>
            Task.FromResult<HarnessReductionCheckpointV1?>(HarnessReductionFold.SeedCheckpoint(executionId) with { ReducerKind = "harness-somethingelse/v1" });

        public Task WriteReducedAsync(NativeRecordBatch batch, HarnessReductionCheckpointV1 checkpoint, CancellationToken cancellationToken) =>
            ((INativeRecordReductionPlane)_inner).WriteReducedAsync(batch, checkpoint, cancellationToken);
    }
}
