using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Authority;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Core.Services.Agents.Recovery.Checkpoints;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using Autofac.Extensions.DependencyInjection;
using CodeSpace.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 3c PRODUCE side — making a running agent's conversation survive the loss of its host.
///
/// <para>Until now the resumable session transcript was captured only at run END, out of the LAUNCHING host's own
/// spool (<c>AgentRunExecutor.CaptureSessionTranscriptAsync</c> reads
/// <c>LocalProcessRunner.ConfigHomePath(handle.SpoolDirectory)</c>). A worker that never launched the run cannot read
/// that spool, and a host that dies takes it with it — so an abandoned agent node was retried COLD even though the
/// node's retry policy had already bought it a fresh attempt.</para>
///
/// <para>Everything here drives the REAL executor, the REAL drain tick, the REAL checkpointer, the REAL artifact
/// store and the REAL fenced row write, and asserts on the rows THIS test owns. The CONSUME side — what the fresh
/// attempt does with a checkpoint — lives in <c>AgentCodeNodeTests</c> and <c>AgentNodeCheckpointRetryFlowTests</c>.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunSessionCheckpointFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _spoolDirs = [];

    public AgentRunSessionCheckpointFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    public void Dispose()
    {
        foreach (var dir in _spoolDirs)
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Theory]
    [InlineData(false)]   // scratch workspace — the executor mounts one, and its Repositories are empty
    [InlineData(true)]    // an authored cwd — no workspace is resolved at all, and the envelope's own path stands
    public async Task A_running_agent_checkpoints_its_live_transcript_through_the_real_tick(bool authoredWorkingDirectory)
    {
        // The produce half, driven end to end: the REAL executor, the REAL drain tick, the REAL checkpointer, the
        // REAL artifact store and the REAL fenced row write. Nothing here seeds a column or computes a path.
        //
        // MUTATION this pins: pass the PRIMARY REPO's directory to the tick instead of the cwd the CLI actually got
        // (which is what AgentRunExecutor.cs did before this rework). Claude keys its session file on the cwd
        // (projects/<sanitized-cwd>/<id>.jsonl), so wherever the two differ the located file never exists and the run
        // silently never checkpoints. BOTH arms here are no-primary-repo shapes, which is what makes the primary-repo
        // directory null and both arms red. The multi-repo shape the review named — a cwd at the workspace ROOT while
        // the primary repo sits in a subdirectory, so both paths are non-null and different — is the same read one
        // step further along, and is not reproducible here without real clones; it is named rather than faked.
        var team = await SeedTeamAsync();
        var run = await SeedQueuedResumableRunAsync(team, authoredWorkingDirectory);
        var harness = new TranscriptWritingHarness(run.SessionId);

        await ExecuteAsync(run.RunId, harness);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var row = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == run.RunId);

        harness.WrittenAt.ShouldNotBeNull($"the harness never wrote a transcript, so this arm proves nothing — cwd was {harness.ObservedWorkingDirectory ?? "null"}");

        // The terminal write RELEASES the checkpoint (it exists for a run whose host dies, and this one landed), so
        // the LIVE stamp is read from the event the run recorded while it was still going, plus the artifact itself.
        var declared = await db.WorkflowArtifactRetention.AsNoTracking()
            .Where(d => d.TeamId == team.TeamId && d.RetentionClass == nameof(ArtifactRetentionClass.SessionTranscriptCheckpoint) && d.HolderId == run.RunId)
            .ToListAsync();

        declared.ShouldNotBeEmpty($"the live tick must have checkpointed the transcript the CLI wrote at {harness.WrittenAt}; nothing was stored for run {run.RunId}");
        declared.ShouldAllBe(d => d.HolderKind == ArtifactSessionTranscriptCheckpointer.CheckpointHolderKind,
            "the declaration names the RUN as its holder, which is what a later collection is diagnosed from");

        var bytes = (await verify.Resolve<IArtifactStore>().GetBytesAsync(team.TeamId, declared[0].ArtifactId, CancellationToken.None)).ShouldNotBeNull();
        Encoding.UTF8.GetString(bytes.Bytes).ShouldContain(run.SessionId, Case.Sensitive,
            "the stored bytes are the agent's own live session file, not an empty or fabricated one");

        row.SessionId.ShouldBe(run.SessionId, "the checkpoint stamps the session id too — a checkpoint no CLI can be pointed at is not resumable");
        row.SessionTranscriptCheckpointArtifactId.ShouldBeNull("the run LANDED, so its terminal write released the checkpoint reference; holding it would pin the artifact Referenced for ever beside the end-of-run transcript");
        row.SessionTranscriptCheckpointAt.ShouldBeNull();
    }

    [Fact]
    public async Task A_streaming_agent_survives_a_checkpoint_that_is_still_uploading()
    {
        // N1. The checkpoint runs OFF the drain tick, and the tick is using this executor's scoped DbContext every
        // 250 ms for its buffered-event flush and its spool-offset write. A checkpointer sharing that context puts
        // two operations on one EF context at once, which EF refuses on whichever statement starts SECOND — as often
        // the tick's, unhandled inside the runner's attach loop, killing a healthy run for a best-effort aid.
        //
        // A single-line agent never shows this: one tick, no overlap. This one streams across several ticks while a
        // deliberately SLOW store upload is in flight, which is what a real agent does all the time.
        // MUTATION: resolve the checkpointer from the executor's own scope instead of a fresh one (drop
        // CheckpointInOwnScopeAsync) → "A second operation was started on this context instance" → red.
        var team = await SeedTeamAsync();
        var run = await SeedQueuedResumableRunAsync(team, authoredWorkingDirectory: false);
        await ExecuteAsync(run.RunId, new TranscriptWritingHarness(run.SessionId, lines: 6), uploadDelay: TimeSpan.FromMilliseconds(700));

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var row = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == run.RunId);

        row.Status.ShouldBe(AgentRunStatus.Succeeded, $"the run must land normally while a checkpoint uploads; it ended {row.Status} — {row.Error ?? "no error"}");
        (await db.WorkflowArtifactRetention.AsNoTracking().CountAsync(d => d.TeamId == team.TeamId && d.HolderId == run.RunId))
            .ShouldBeGreaterThan(0, "this arm proves nothing unless a checkpoint was actually taken while the ticks kept firing");
        (await db.AgentRunEvent.AsNoTracking().CountAsync(e => e.AgentRunId == run.RunId)).ShouldBeGreaterThan(1, "the tick's own event flush must have kept working throughout");
    }

    [Fact]
    public async Task The_last_checkpoint_lands_even_when_the_agent_exits_immediately()
    {
        // N2. The upload is started fire-and-forget from the tick, so without a drain the ROUND does not wait for it.
        // That is not merely untidy: the checkpoint's stamp is fenced on the run still being Running, so an upload
        // still in flight when the terminal write lands has its stamp REFUSED — and the last minute of conversation,
        // the most valuable minute a host loss could have taken, is silently dropped. The agent here exits the
        // instant it finishes writing, so there is no slack to hide behind.
        // MUTATION: remove the DrainSessionTranscriptCheckpointAsync call from the live path → the round returns in
        // about a second while a four-second upload is still running → red.
        var upload = TimeSpan.FromSeconds(2);   // comfortably inside AgentRunExecutor.SessionCheckpointDrainBudget
        var team = await SeedTeamAsync();
        var run = await SeedQueuedResumableRunAsync(team, authoredWorkingDirectory: false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await ExecuteAsync(run.RunId, new TranscriptWritingHarness(run.SessionId, lines: 2, trailer: ""), uploadDelay: upload);
        clock.Stop();

        using var verify = _fixture.BeginScope();

        (await verify.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking()
            .CountAsync(d => d.TeamId == team.TeamId && d.HolderId == run.RunId))
            .ShouldBeGreaterThan(0, "this arm proves nothing unless a checkpoint was actually in flight when the agent exited");

        clock.Elapsed.ShouldBeGreaterThanOrEqualTo(upload,
            $"the round returned in {clock.ElapsedMilliseconds}ms while a {upload.TotalSeconds}s checkpoint was still uploading — its stamp is fenced on the run being Running, so it would be refused by the terminal write and the last conversation lost");
    }

    [Fact]
    public async Task A_run_that_did_not_opt_in_writes_no_checkpoint_at_all()
    {
        // The other half of the produce gate, and the reason it is worth its own test: without it every workflow and
        // supervisor agent on the fleet would upload a copy of its transcript once a minute for a continuation that
        // can never be bought. MUTATION: build the tick unconditionally → a declaration appears → red.
        var team = await SeedTeamAsync();
        var run = await SeedQueuedResumableRunAsync(team, authoredWorkingDirectory: false, checkpointSessionTranscript: false);

        await ExecuteAsync(run.RunId, new TranscriptWritingHarness(run.SessionId));

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking()
            .CountAsync(d => d.TeamId == team.TeamId && d.HolderId == run.RunId))
            .ShouldBe(0, "a run nobody will ever continue must not pay for, or store, a checkpoint");
    }

    [Fact]
    public async Task A_landed_run_releases_its_checkpoint_so_the_reaper_can_collect_it()
    {
        // The leak the terminal write closes. A checkpoint is Referenced while the column names it, and Referenced is
        // TERMINAL in the retention ledger — so a run that landed normally would pin its last transcript copy for
        // ever, beside the end-of-run transcript its own result already carries.
        // MUTATION: drop the two NULL assignments from CompleteCoreAsync's terminal UPDATE → the oracle still answers
        // Referenced → red.
        var team = await SeedTeamAsync();
        var run = await SeedQueuedResumableRunAsync(team, authoredWorkingDirectory: false);

        await ExecuteAsync(run.RunId, new TranscriptWritingHarness(run.SessionId));

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var declared = await db.WorkflowArtifactRetention.AsNoTracking()
            .Where(d => d.TeamId == team.TeamId && d.HolderId == run.RunId).Select(d => d.ArtifactId).ToListAsync();

        declared.ShouldNotBeEmpty("this arm needs a checkpoint to have been taken, or it proves nothing about releasing one");

        var oracle = verify.Resolve<IArtifactReferenceOracle>();
        foreach (var artifactId in declared)
            (await oracle.ClassifyAsync(db, artifactId, CancellationToken.None))
                .ShouldBe(ArtifactReferenceVerdict.Unreferenced, $"artifact {artifactId} is still named by a live column after its run landed, so the reaper can never collect it");
    }

    [Fact]
    public async Task The_checkpoint_stamp_is_fenced_and_writes_the_session_id_once()
    {
        // The raw fenced UPDATE behind every checkpoint, exercised against Postgres rather than a fake: it must land
        // under the CURRENT owner and fence, land NOTHING under a superseded one, and never overwrite a session id
        // the row already carries with a null.
        // MUTATION: drop the fence_epoch predicate from the statement → the superseded arm reds; replace the
        // COALESCE with a bare assignment → the last assertion reds.
        var team = await SeedTeamAsync();
        var owner = await SeedRunningOwnedRunAsync(team);

        using var scope = _fixture.BeginScope();
        var runs = scope.Resolve<IAgentRunService>();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var first = new SessionTranscriptCheckpoint(await StoreCheckpointAsync(team.TeamId, "one\n"), DateTimeOffset.UtcNow, 4);
        (await runs.StampSessionTranscriptCheckpointAsync(owner, first, "s-live", CancellationToken.None)).ShouldBeTrue("the live owner's stamp must land");

        var stamped = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == owner.RunId);
        stamped.SessionTranscriptCheckpointArtifactId.ShouldBe(first.ArtifactId);
        stamped.SessionTranscriptCheckpointAt.ShouldNotBeNull();
        stamped.SessionId.ShouldBe("s-live", "a checkpoint no CLI can be pointed at is not resumable, so the id rides the same statement");

        var superseded = new SessionTranscriptCheckpoint(await StoreCheckpointAsync(team.TeamId, "two\n"), DateTimeOffset.UtcNow, 4);
        (await runs.StampSessionTranscriptCheckpointAsync(owner with { Epoch = owner.Epoch - 1 }, superseded, "s-stale", CancellationToken.None))
            .ShouldBeFalse("a worker whose ownership was reclaimed must not point a live run's recovery at its own stale conversation");

        var later = new SessionTranscriptCheckpoint(await StoreCheckpointAsync(team.TeamId, "three\n"), DateTimeOffset.UtcNow, 6);
        (await runs.StampSessionTranscriptCheckpointAsync(owner, later, null, CancellationToken.None)).ShouldBeTrue();

        var final = await db.AgentRun.AsNoTracking().SingleAsync(r => r.Id == owner.RunId);
        final.SessionTranscriptCheckpointArtifactId.ShouldBe(later.ArtifactId, "the newest checkpoint supersedes the last");
        final.SessionId.ShouldBe("s-live", "a later tick that observed no session id must not erase the one the row already carries");
    }

    [Fact]
    public async Task A_slow_destination_still_gets_its_checkpoint_stored()
    {
        // The reason the upload's deadline is NOT the drain's. One checkpoint reads and uploads up to the 32 MiB
        // capture cap, so on a throttled or cross-region destination it can legitimately outlast the few seconds a
        // finished run may be held open — and if the two bounds were one number, every checkpoint of exactly the
        // long conversations this feature protects would be cancelled by its own deadline and the run would
        // silently stop being recoverable.
        //
        // Eight seconds is past the drain bound (the round lands without it) and well inside the upload's, so the
        // checkpoint must still be STORED.
        // MUTATION: give the upload the drain's 5s budget → its own CTS cancels it → nothing is ever stored → red.
        var slow = TimeSpan.FromSeconds(8);
        var team = await SeedTeamAsync();
        var run = await SeedQueuedResumableRunAsync(team, authoredWorkingDirectory: false);

        await ExecuteAsync(run.RunId, new TranscriptWritingHarness(run.SessionId, lines: 2, trailer: ""), uploadDelay: slow);

        // The round has already landed by here — the drain gave up at its own bound — so the checkpoint is still on
        // its way. Wait for it, bounded and loud: the predicate starts FALSE, because the round returned before the
        // upload could possibly have finished.
        var deadline = DateTimeOffset.UtcNow + slow + TimeSpan.FromSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var poll = _fixture.BeginScope();
            if (await poll.Resolve<CodeSpaceDbContext>().WorkflowArtifactRetention.AsNoTracking().AnyAsync(d => d.TeamId == team.TeamId && d.HolderId == run.RunId)) return;

            await Task.Delay(250);
        }

        throw new Xunit.Sdk.XunitException(
            $"no checkpoint was ever stored for run {run.RunId} against a {slow.TotalSeconds}s destination. The upload's own deadline (AgentRunExecutor.SessionCheckpointUploadBudget) must be sized for the 32 MiB capture cap, not for how long a landing may be deferred — check whether it was collapsed into SessionCheckpointDrainBudget.");
    }

    [Fact]
    public async Task A_wedged_store_cannot_defer_the_terminal_write_past_the_checkpoint_budget()
    {
        // The other half of waiting for the last checkpoint: the wait sits in FRONT of the run's terminal write, so
        // an unbounded one would let a wedged storage backend hold a finished run open for as long as its client is
        // willing to hang. The checkpoint carries its own deadline and the drain backstops it, so a store that
        // ignores cancellation still cannot defer the landing.
        // MUTATION: drop SessionCheckpointDrainBudget from the drain's WaitAsync → the round waits for the upload's
        // own thirty-second deadline instead → red.
        var wedged = TimeSpan.FromSeconds(20);
        var team = await SeedTeamAsync();
        var run = await SeedQueuedResumableRunAsync(team, authoredWorkingDirectory: false);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        await ExecuteAsync(run.RunId, new TranscriptWritingHarness(run.SessionId, lines: 2, trailer: ""), uploadDelay: wedged);
        clock.Stop();

        clock.Elapsed.ShouldBeLessThan(wedged,
            $"the round took {clock.Elapsed.TotalSeconds:F1}s against a {wedged.TotalSeconds}s wedged store — a best-effort checkpoint must never hold a finished run's terminal write open");

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == run.RunId)).Status
            .ShouldBe(AgentRunStatus.Succeeded, "and the run must still land");
    }

    [Theory]
    [InlineData(true)]    // a CHECKPOINT ref — best-effort, so an unreadable one must degrade
    [InlineData(false)]   // a CAPTURED ref — written by an attempt that finished, so an unreadable one is a fault
    public async Task An_unreadable_transcript_ref_degrades_only_when_it_is_a_checkpoint(bool isCheckpoint)
    {
        // The two refs are resolved under opposite policies, and the difference decides whether a lost host costs
        // the tenant an attempt. A captured transcript was written by an attempt that FINISHED, so an unreadable one
        // is a genuine fault and cold-starting a named session silently would hide it. A checkpoint is best-effort by
        // construction — its blob may have been collected, its destination may be unreachable — and this task is
        // already a RETRY of a lost host, so failing it would spend the very attempt the checkpoint exists to improve
        // on the one fault that says nothing about the work.
        // MUTATION: delete the `if (!task.RestoredTranscriptIsCheckpoint)` branch (fail closed for both) → the
        // checkpoint arm reds; make both degrade → the captured arm reds.
        var team = await SeedTeamAsync();
        var priorRunId = Guid.NewGuid();
        var absent = Guid.NewGuid();   // never written to the artifact store
        var harness = new TranscriptWritingHarness("s-unreadable");

        var run = await SeedQueuedResumableRunAsync(team, authoredWorkingDirectory: false, task => task with
        {
            ResumeFromSessionId = "s-lost-host",
            RestoredTranscriptArtifactId = absent,
            RestoredTranscriptIsCheckpoint = isCheckpoint,
            ResumedFromCheckpointAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            ResumedFromAgentRunId = priorRunId,
        });

        await ExecuteAsync(run.RunId, harness);

        using var verify = _fixture.BeginScope();
        var row = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(r => r.Id == run.RunId);

        if (!isCheckpoint)
        {
            row.Status.ShouldBe(AgentRunStatus.Failed, "an unreadable CAPTURED transcript is a real fault — failing closed is what stops a named session silently cold-starting");
            harness.Invocations.ShouldBeEmpty("and the agent must never have been launched at all");
            return;
        }

        row.Status.ShouldBe(AgentRunStatus.Succeeded, $"an unreadable checkpoint must cost the conversation, never the attempt; the run ended {row.Status} — {row.Error ?? "no error"}");

        var launched = harness.Invocations.ShouldHaveSingleItem();
        launched.RestoredTranscriptArtifactId.ShouldBeNull("the ref that resolved to nothing must not ride into the invocation");
        launched.RestoredTranscript.ShouldBeNull();
        launched.ResumeFromSessionId.ShouldBeNull("a --resume naming a session whose transcript was never restored cold-starts in the CLI anyway, silently — so it goes with the ref");
        launched.ResumedFromCheckpointAt.ShouldBeNull("the permanent confinement record must not claim a continuation this attempt never had");
        launched.ResumedFromAgentRunId.ShouldBeNull();
        launched.Goal.ShouldContain(AgentRetryContinuity.LostHostCheckpointUnreadableHint, Case.Sensitive,
            "an agent that was going to be handed a conversation must be told it is not getting one");

        JsonSerializer.Deserialize<SandboxConfinement>(row.SandboxConfinementJson!, AgentJson.Options)!.ResumedFromCheckpointAt
            .ShouldBeNull("and the run's own record must agree with what the agent was actually given");
    }

    [Fact]
    public void The_production_executor_is_wired_to_the_checkpointer()
    {
        // The whole slice is INERT if this wire is missing, and nothing else would say so: the checkpointer is an
        // optional constructor dependency (so a hand-built test double need not know about it), the tick no-ops on a
        // null one, and every other test in this class hands the executor its own collaborators. So the one thing no
        // other assertion covers is whether the CONTAINER fills it — a capability with no caller.
        // MUTATION: drop the IScopedDependency marker from IAgentSessionTranscriptCheckpointer (DI here is marker
        // scanning, so nothing else registers it) → the resolve below fails and the injected field is null.
        using var scope = _fixture.BeginScope();

        scope.Resolve<IAgentSessionTranscriptCheckpointer>().ShouldBeOfType<ArtifactSessionTranscriptCheckpointer>(
            "the marker on the interface is what CodeSpaceModule.RegisterDependency scans for; without it no implementation is registered at all");

        var executor = scope.Resolve<IAgentRunExecutor>().ShouldBeOfType<AgentRunExecutor>();
        var injected = typeof(AgentRunExecutor)
            .GetField("_sessionCheckpointer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .ShouldNotBeNull("the field was renamed — update this wiring check rather than deleting it")
            .GetValue(executor);

        injected.ShouldNotBeNull("the container must fill the executor's optional checkpointer; unfilled, every running agent silently stops being continuable after a host loss and no test elsewhere would notice");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>The seeded team plus its Owner's live principal identity — the four values a standalone authority receipt is minted from.</summary>
    private readonly record struct SeededTeam(Guid TeamId, Guid MembershipId, Guid UserId, Guid SecurityStamp);


    /// <summary>Drive the REAL <see cref="AgentRunExecutor.ExecuteAsync(Guid, CancellationToken)"/> — the launch path whose drain tick owns the checkpoint — with the CONTAINER's own checkpointer, so the produce side runs the shape production runs.</summary>
    private async Task ExecuteAsync(Guid runId, IAgentHarness harness, TimeSpan? uploadDelay = null)
    {
        using var outer = _fixture.BeginScope();

        // The executor resolves its checkpointer from a DI scope of its OWN, per checkpoint — so a test that wants a
        // slow store decorates the registration those scopes inherit, never an instance handed to the constructor.
        // Anything else would stop measuring the production shape at exactly the seam under test.
        using var scope = uploadDelay is { } delay
            ? outer.BeginLifetimeScope(b => { b.RegisterInstance(new UploadDelay(delay)); b.RegisterType<SlowCheckpointer>().As<IAgentSessionTranscriptCheckpointer>().InstancePerLifetimeScope(); })
            : outer.BeginLifetimeScope();

        await NewExecutor(scope, harness, scope.Resolve<ISandboxRunnerRegistry>(), scope.Resolve<IAgentSessionTranscriptCheckpointer>()).ExecuteAsync(runId, CancellationToken.None);
    }

    /// <summary>
    /// An <see cref="IServiceScopeFactory"/> rooted at THIS scope, so a per-test registration override is visible to
    /// the scopes the executor creates for its own checkpoints.
    ///
    /// <para>The container's own factory is a singleton holding the ROOT lifetime scope, so every scope it makes is a
    /// child of the container and sees nothing a test registered. The shape under test is unchanged — one fresh
    /// scope, and therefore one fresh <c>DbContext</c>, per checkpoint — only its parent moves.</para>
    /// </summary>
    private sealed class ScopedServiceScopeFactory : IServiceScopeFactory
    {
        private readonly ILifetimeScope _scope;

        public ScopedServiceScopeFactory(ILifetimeScope scope) => _scope = scope;

        public IServiceScope CreateScope() => new Scope(_scope.BeginLifetimeScope());

        private sealed class Scope : IServiceScope
        {
            private readonly ILifetimeScope _child;

            public Scope(ILifetimeScope child) { _child = child; ServiceProvider = new AutofacServiceProvider(child); }

            public IServiceProvider ServiceProvider { get; }

            public void Dispose() => _child.Dispose();
        }
    }

    /// <summary>How long a decorated artifact write is held open — long enough to still be in flight when the next drain tick fires, and when the agent exits.</summary>
    private sealed record UploadDelay(TimeSpan Value);

    /// <summary>
    /// The REAL checkpointer with a slow DATABASE operation held open in front of it — so a checkpoint really is
    /// mid-upload while the drain tick keeps firing and while the run lands, and the checkpoint it finally takes is
    /// the production one.
    ///
    /// <para>A bare <c>Task.Delay</c> would not do for the concurrency arm: the defect there is two operations on one
    /// EF context, so the context has to be BUSY for the window, not merely the thread idle. <c>pg_sleep</c> on the
    /// SCOPED context is exactly that — harmless when the checkpoint runs in a scope of its own, and a guaranteed
    /// collision with the drain tick's own statement when it does not.</para>
    ///
    /// <para>Registered as a plain override rather than a decorator: the executor resolves its checkpointer from a
    /// scope IT creates, and a decorator registered on this test's scope does not reach that far.</para>
    /// </summary>
    private sealed class SlowCheckpointer : IAgentSessionTranscriptCheckpointer
    {
        private readonly ArtifactSessionTranscriptCheckpointer _inner;
        private readonly CodeSpaceDbContext _db;
        private readonly UploadDelay _delay;

        public SlowCheckpointer(ArtifactSessionTranscriptCheckpointer inner, CodeSpaceDbContext db, UploadDelay delay) { _inner = inner; _db = db; _delay = delay; }

        public async Task<SessionTranscriptCheckpoint?> CheckpointAsync(SessionTranscriptCheckpointRequest request, CancellationToken cancellationToken)
        {
            await _db.Database.ExecuteSqlRawAsync($"SELECT pg_sleep({_delay.Value.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)})", cancellationToken).ConfigureAwait(false);

            return await _inner.CheckpointAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private static AgentRunExecutor NewExecutor(ILifetimeScope scope, IAgentHarness harness, ISandboxRunnerRegistry runners, IAgentSessionTranscriptCheckpointer? checkpointer)
    {
        return new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            new AgentHarnessRegistry([harness]),
            new HarnessModelReconciler(new AgentHarnessRegistry([harness]), scope.Resolve<CodeSpace.Core.Services.Agents.ModelCredentials.IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            runners,
            scope.Resolve<CodeSpace.Core.Services.Agents.Workspace.IAgentWorkspaceResolver>(),
            scope.Resolve<IModelCredentialResolver>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Workspace.IWorkspaceProviderRegistry>(),
            scope.Resolve<IAgentRunCompletionNotifier>(),
            new ScopedServiceScopeFactory(scope),
            scope.Resolve<CodeSpaceDbContext>(),
            scope.Resolve<CodeSpace.Core.Services.Review.IStructuredCritic>(),
            scope.Resolve<IArtifactOffloader>(),
            scope.Resolve<IArtifactStore>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IArtifactManifestStore>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Capture.ICaptureIntentService>(),
            scope.Resolve<IEnumerable<CodeSpace.Core.Services.Agents.Publish.IPublishGuard>>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRunExecutor>.Instance,
            sessionCheckpointer: checkpointer);
    }

    /// <summary>Store a transcript in the REAL team-scoped artifact store and hand back its id — the same store the checkpointer writes to, so the resume's resolve reads production bytes.</summary>
    private async Task<Guid> StoreCheckpointAsync(Guid teamId, string transcript)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IArtifactStore>().PutAsync(teamId, Encoding.UTF8.GetBytes(transcript), "application/x-ndjson", CancellationToken.None);
    }

    /// <summary>
    /// Seed a QUEUED standalone run the executor can actually launch, opted in (or not) to continuation.
    ///
    /// <para><paramref name="authoredWorkingDirectory"/> is the divergence under test. Both arms produce a cwd that
    /// is NOT the primary repository's directory — which is what the tick used to be handed. With no repository the
    /// executor either mounts a scratch workspace (whose <c>Repositories</c> are empty, so the primary-repo directory
    /// is null) or, when the envelope authors its own directory, resolves no workspace at all and keeps that path.
    /// The multi-repo shape the review named is the same read one step further along — its cwd is the workspace
    /// ROOT while the primary repo sits in a subdirectory — and is not reproducible here without real clones, so it
    /// is named rather than faked.</para>
    /// </summary>
    private async Task<(Guid RunId, string SessionId)> SeedQueuedResumableRunAsync(SeededTeam team, bool authoredWorkingDirectory, Func<AgentTask, AgentTask>? shape = null, bool checkpointSessionTranscript = true)
    {
        var runId = Guid.NewGuid();
        var sessionId = $"s-{Guid.NewGuid():N}";
        var cwd = authoredWorkingDirectory ? NewDirectory() : null;

        var task = new AgentTask
        {
            Goal = "Fix the failing billing tests", Harness = TranscriptWritingHarness.HarnessKind, TimeoutSeconds = 120,
            CheckpointSessionTranscript = checkpointSessionTranscript, WorkspaceDirectory = cwd, ExecutionAuthority = AuthorityOf(team, runId),
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = team.TeamId, Harness = TranscriptWritingHarness.HarnessKind, Status = AgentRunStatus.Queued,
            TaskJson = JsonSerializer.Serialize(shape is null ? task : shape(task), AgentJson.Options),
        });
        await db.SaveChangesAsync();

        return (runId, sessionId);
    }

    /// <summary>Seed a RUNNING run this test class owns the observation token for — the shape the fenced stamp is written under.</summary>
    private async Task<AgentRunOwnerToken> SeedRunningOwnedRunAsync(SeededTeam team)
    {
        var runId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = team.TeamId, Harness = TranscriptWritingHarness.HarnessKind, Status = AgentRunStatus.Running,
            FenceEpoch = 3, OwnerId = ownerId, StartedAt = DateTimeOffset.UtcNow, HeartbeatAt = DateTimeOffset.UtcNow,
            LeaseExpiresAt = DateTimeOffset.UtcNow + AgentRunLiveness.Window,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = "Fix the failing billing tests", Harness = TranscriptWritingHarness.HarnessKind }, AgentJson.Options),
        });
        await db.SaveChangesAsync();

        return new AgentRunOwnerToken(runId, ownerId, 3);
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-checkpoint-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _spoolDirs.Add(directory);

        return directory;
    }

    private async Task<SeededTeam> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        var user = new User { Id = userId, Email = $"checkpoint-{userId:N}@test.local", Name = $"checkpoint-{userId:N}" };
        db.User.Add(user);

        var teamId = Guid.NewGuid();
        var membershipId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"checkpoint-{teamId:N}", Name = "Checkpoint Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = membershipId, TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();

        var stamp = (await db.User.AsNoTracking().SingleAsync(u => u.Id == userId)).SecurityStamp;

        return new SeededTeam(teamId, membershipId, userId, stamp);
    }

    /// <summary>The standalone authority receipt a launch mints, so the executor can claim and run the seeded row exactly as production would.</summary>
    private static AgentExecutionAuthority AuthorityOf(SeededTeam team, Guid logicalRunId) => new()
    {
        Version = ExecutionAuthorityService.ReceiptVersion,
        PolicyVersion = ExecutionAuthorityService.PolicyVersion,
        TeamId = team.TeamId,
        LogicalRunId = logicalRunId,
        SourceKind = "standalone",
        DefinitionHash = "",
        GrantedCeiling = AgentAutonomyPolicy.DeploymentCeiling,
        IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        Subjects = [new AgentAuthoritySubject { Kind = "launcher", UserId = team.UserId, SecurityStamp = team.SecurityStamp, MembershipId = team.MembershipId, IssuedRole = TeamRole.Owner, Permission = TeamPermissions.RunsLaunch, GlobalAdmin = false }],
    };

    /// <summary>
    /// A harness whose agent really writes a session transcript where the CLI would, and then stays alive long enough
    /// for the drain tick to find it.
    ///
    /// <para>The LOCATE is delegated to the production <see cref="ClaudeCodeHarness"/>, in both directions: the
    /// executor asks it where the transcript is, and the script below writes it exactly there. The layout
    /// (<c>projects/&lt;sanitized-cwd&gt;/&lt;id&gt;.jsonl</c>) is never restated by this test — a test that computed
    /// the path itself would keep passing after production started computing a different one, which is the whole
    /// defect this class exists to catch.</para>
    ///
    /// <para>The script writes the FILE first and prints the session line second, so by the time the fold knows the
    /// session id the file it names is already on disk — the tick is then deterministic rather than a race with the
    /// first poll.</para>
    /// </summary>
    private sealed class TranscriptWritingHarness : IAgentHarness, IAgentSessionTranscript
    {
        public const string HarnessKind = "scripted";
        private const string ConfigHomeEnvVar = "CS_TEST_CONFIG_DIR";
        private static readonly ClaudeCodeHarness Layout = new();

        private readonly string _sessionId;
        private readonly int _lines;
        private readonly string _trailer;

        /// <param name="sessionId">The session the script names on every line, so the fold can address the transcript.</param>
        /// <param name="lines">How many lines the agent streams. More than one makes the drain tick fire REPEATEDLY while a checkpoint may be in flight — the overlap a single-line agent never produces.</param>
        /// <param name="trailer">What the agent does after its last line. Empty means it exits IMMEDIATELY, leaving the executor no slack to finish an upload in.</param>
        public TranscriptWritingHarness(string sessionId, int lines = 1, string trailer = "sleep 2")
        {
            _sessionId = sessionId;
            _lines = lines;
            _trailer = trailer;
        }

        /// <summary>The cwd the executor actually handed the CLI — reported so a failing arm can say WHICH directory it was given.</summary>
        public string? ObservedWorkingDirectory { get; private set; }

        /// <summary>The config-home-relative path the agent wrote to, or null when the production locate could not address one.</summary>
        public string? WrittenAt { get; private set; }

        /// <summary>The task each launch was built from — the call site where "what the agent was actually given" is either true or it is not.</summary>
        public List<AgentTask> Invocations { get; } = [];

        public string Kind => HarnessKind;
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = ["test-model"];

        public string? SessionTranscriptRelativePath(string configHome, string? workspaceDirectory, string? sessionId) =>
            ((IAgentSessionTranscript)Layout).SessionTranscriptRelativePath(configHome, workspaceDirectory, sessionId);

        public SandboxSpec BuildInvocation(AgentTask task)
        {
            Invocations.Add(task);
            ObservedWorkingDirectory = task.WorkspaceDirectory;
            WrittenAt = SessionTranscriptRelativePath("", task.WorkspaceDirectory, _sessionId);

            var line = $"{{\"type\":\"assistant\",\"session_id\":\"{_sessionId}\",\"text\":\"working\"}}";
            // The transcript is written BEFORE the first line is printed, so by the time the fold knows the session
            // id the file it names is already on disk — the tick is then deterministic instead of racing the first
            // poll. Each later line APPENDS to it, so a multi-line agent keeps the file growing across ticks exactly
            // as a real one does.
            var target = $"\"${ConfigHomeEnvVar}/{WrittenAt}\"";
            var stream = string.Join(" && ", Enumerable.Range(0, _lines).Select(_ => $"printf '%s\\n' '{line}' >> {target} && printf '%s\\n' '{line}' && sleep 0.3"));
            var script = WrittenAt is null
                ? "printf 'no addressable transcript\\n'; sleep 1"
                : $"mkdir -p \"${ConfigHomeEnvVar}/{Path.GetDirectoryName(WrittenAt)!.Replace('\\', '/')}\" && : > {target} && {stream}{(_trailer.Length == 0 ? "" : " && " + _trailer)}";

            return new SandboxSpec { Command = "/bin/sh", Args = ["-c", script], WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = 120, ConfigHomeEnvVars = [ConfigHomeEnvVar] };
        }

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine) =>
            string.IsNullOrWhiteSpace(rawLine) ? [] : [new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim(), Data = TryParse(rawLine) }];

        public IAgentEventFolder CreateFolder() => ScriptedFolders.Result();

        private static JsonElement? TryParse(string rawLine)
        {
            try { return JsonSerializer.Deserialize<JsonElement>(rawLine); }
            catch (JsonException) { return null; }
        }
    }

}
