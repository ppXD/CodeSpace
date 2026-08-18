using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.ModelCredentials;
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
/// Drives the real executor, the real <see cref="LocalProcessRunner"/> and real Postgres to prove the DUAL WRITE: the
/// normalized event log lands exactly as before, and beside it every native frame the harness produced lands as its
/// own durable record — including the frames the parser DROPS and the frames it THROWS on, which are precisely the
/// ones the normalized log has no row for at all.
///
/// <para>The run's own outcome is asserted IDENTICAL with and without the plane in every case. Capture is a dual
/// write, not a cutover: neither recording a frame nor failing to record one may change what an Agent Run resolves
/// to, and a guard that only holds where the plane happens to be deployed is a guard that fails open.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NativeRecordDualWriteFlowTests
{
    private readonly PostgresFixture _fixture;

    public NativeRecordDualWriteFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The losslessness pin, end to end. Two lines: one the parser understands and one it silently drops. The
    /// normalized log can only show the first; both must have a native record, and the run must still succeed.
    /// </summary>
    [Fact]
    public async Task Every_native_frame_is_recorded_even_the_one_the_parser_drops()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new SelectiveHarness("printf 'keep me\\nDROP\\n'"));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None);
        run.Status.ShouldBe(AgentRunStatus.Succeeded,
            customMessage: "a frame the parser had nothing to say about is not a failure — it is the silent drop this plane makes countable");

        var normalized = await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None);
        normalized.Select(candidate => candidate.Text).ShouldBe(new[] { "keep me" },
            customMessage: "the normalized log is unchanged by this slice, and its silence about the dropped line is the loss this plane exists to end");

        var records = await db.WorkflowRunNativeRecord.AsNoTracking()
            .Where(candidate => candidate.AgentRunId == runId).OrderBy(candidate => candidate.Ordinal).ToListAsync();

        records.Select(record => record.InlinePayload).ShouldBe(new[] { "keep me", "DROP" },
            customMessage: "one record per delivered frame, in stream order — the dropped line is exactly the case that must still be here");
        records.Select(record => record.Ordinal).ShouldBe(new long[] { 0, 1 });
        records.Select(record => record.Normalization).ShouldBe(new[]
        {
            NativeRecordNormalization.Projected, NativeRecordNormalization.Unrecognized,
        });

        var projections = await db.WorkflowRunSemanticEvent.AsNoTracking()
            .Where(candidate => candidate.AgentRunId == runId).ToListAsync();

        var projection = projections.ShouldHaveSingleItem();
        projection.SourceNativeRecordIds.ShouldBe(new[] { records[0].Id },
            customMessage: "a projection names the exact frame it was folded from, and never replaces it");
        projection.ProjectionQuality.ShouldBe(SemanticProjectionQuality.Derived);
        projection.EventType.ShouldBe("https://codespace.dev/agent/v1/assistant-message");
    }

    /// <summary>
    /// The containment question, asked of BOTH deployments at once. A parser that throws used to take the run down; a
    /// plane that swallowed the throw would resolve that same run Succeeded — and, since the plane is optional, would
    /// do it differently depending on whether the shadow plane is deployed. So the throw still propagates, and the
    /// only thing capture adds is the durable frame that says WHICH line could not be read.
    /// </summary>
    [Fact]
    public async Task A_frame_the_parser_cannot_read_resolves_the_run_the_same_with_and_without_the_plane()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var captured = await CreateScriptedRunAsync(teamId);
        var bare = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(captured, new SelectiveHarness("printf 'keep me\\nTHROW\\n'"));
        await ExecuteAsync(bare, new SelectiveHarness("printf 'keep me\\nTHROW\\n'"), withPlane: false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var runs = scope.Resolve<IAgentRunService>();

        var withPlane = await runs.GetAsync(captured, CancellationToken.None);
        var withoutPlane = await runs.GetAsync(bare, CancellationToken.None);

        withoutPlane.Status.ShouldBe(AgentRunStatus.Failed,
            customMessage: "this is the pre-plane behaviour being pinned: an unreadable frame propagates out of the observe loop and the executor resolves the run Failed");
        withPlane.Status.ShouldBe(withoutPlane.Status,
            customMessage: "capture must be invisible to the run's outcome — a plane that swallowed the throw would make the same harness bug succeed here and fail on a deployment without the plane");
        withPlane.Error.ShouldBe(withoutPlane.Error,
            customMessage: "the run is handed the PARSER's failure either way — a capture-shaped message here would mean the plane replaced the diagnosis it was supposed to preserve");

        (await db.WorkflowRunNativeRecord.CountAsync(candidate => candidate.AgentRunId == bare)).ShouldBe(0);

        var failed = await db.WorkflowRunNativeRecord.AsNoTracking()
            .Where(candidate => candidate.AgentRunId == captured && candidate.Normalization == NativeRecordNormalization.Failed)
            .SingleAsync();

        failed.InlinePayload.ShouldBe("THROW",
            customMessage: "the frame is flushed on the way out — a throw unwinds the round, so a record left buffered would be the hole this plane exists to end");
        failed.NormalizationErrorCode.ShouldBe(AgentNativeRecordPump.NormalizationThrewErrorCode);
        failed.NormalizationErrorMessage.ShouldNotBeNullOrWhiteSpace(
            customMessage: "a Failed marker with no reason is a hole with a label on it");
    }

    /// <summary>
    /// The isolation blocker, driven through the real plane, the real 0137 trigger and real Postgres. A stale worker
    /// fence is REFUSED by design — it is the reclaim-for-reattach case — so the refusal must cost the run nothing.
    /// It costs it everything if the plane writes on the run's own unit of work: the staged execution and attempt stay
    /// Added in the shared tracker and the run's very next save replays them, which is a successful Agent Run turned
    /// into Failed/executor-error by a shadow plane. <see cref="ICaptureIntentService.OpenAsync"/> IS that next save.
    /// </summary>
    [Fact]
    public async Task A_refused_plane_open_leaves_the_runs_own_unit_of_work_untouched()
    {
        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var plane = scope.Resolve<INativeRecordPlane>();

        var refusal = await Should.ThrowAsync<DbUpdateException>(() => plane.OpenAsync(StaleFenceRequest(teamId, runId), CancellationToken.None));

        refusal.GetBaseException().Message.ShouldContain("stale worker fence",
            customMessage: "the test is only meaningful if 0137's fence guard is what refused the open — any other failure means the staging never reached the database");

        db.ChangeTracker.Entries().ShouldBeEmpty(
            customMessage: "a refused capture must not leave rows staged in the RUN's tracker; the plane writes on its own unit of work precisely so this cannot happen");

        await Should.NotThrowAsync(() => scope.Resolve<ICaptureIntentService>()
            .OpenAsync(runId, teamId, null, 1, null, CancellationToken.None));
    }

    /// <summary>
    /// The capture is keyed to the durable execution identity 0137 provides — so the frames of a run are attributable
    /// to the physical process that produced them, which is what a per-attempt cost or log geometry needs and what a
    /// single mutable agent_run row can never express.
    /// </summary>
    [Fact]
    public async Task Capture_opens_a_harness_execution_and_closes_the_process_it_recorded()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new SelectiveHarness("printf 'one line\\n'"));

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var execution = await db.WorkflowRunHarnessExecution.AsNoTracking().SingleAsync(candidate => candidate.AgentRunId == runId);
        execution.Generation.ShouldBe(1);
        execution.HarnessTypeKey.ShouldBe("scripted/v1",
            customMessage: "the adapter identity is snapshotted so a row read a year later is interpretable against the adapter that wrote it");
        execution.RunnerKind.ShouldBe("local");
        execution.AttemptCount.ShouldBe(1, customMessage: "the head is advanced by the appended attempt's own trigger, never by the writer");

        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().SingleAsync(candidate => candidate.AgentRunId == runId);
        attempt.AttemptOrdinal.ShouldBe(1);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Exited,
            customMessage: "the observer saw this process exit, so recording it as anything else would discard a fact it actually has");
        attempt.ExitCode.ShouldBe(0);
        attempt.RunnerLocatorJson.ShouldContain("spoolKey", customMessage: "the locator is the backend's own opaque address for the process, never a column shared code interprets");

        var records = await db.WorkflowRunNativeRecord.AsNoTracking().Where(candidate => candidate.AgentRunId == runId).ToListAsync();
        records.ShouldAllBe(record => record.ExecutionId == execution.Id && record.AttemptId == attempt.Id);
        records.ShouldAllBe(record => record.Channel == NativeRecordChannel.Stdout);
    }

    /// <summary>
    /// Absent the plane the streaming path must be exactly what it was, and must leave no half of an execution
    /// identity behind. This is the safety half of a dual write: the executor takes the plane as an optional
    /// dependency, and a deployment (or a hand-built double) without one runs unchanged rather than differently. The
    /// unreadable-frame half of the same claim is pinned by
    /// <see cref="A_frame_the_parser_cannot_read_resolves_the_run_the_same_with_and_without_the_plane"/>.
    /// </summary>
    [Fact]
    public async Task Without_the_plane_the_run_and_its_normalized_log_are_unchanged()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var runId = await CreateScriptedRunAsync(teamId);

        await ExecuteAsync(runId, new SelectiveHarness("printf 'keep me\\nDROP\\n'"), withPlane: false);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        (await scope.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Succeeded);
        (await scope.Resolve<IAgentRunService>().GetEventsAsync(runId, teamId, 0, CancellationToken.None))
            .Select(candidate => candidate.Text).ShouldBe(new[] { "keep me" });

        (await db.WorkflowRunNativeRecord.CountAsync(candidate => candidate.AgentRunId == runId)).ShouldBe(0);
        (await db.WorkflowRunHarnessExecution.CountAsync(candidate => candidate.AgentRunId == runId)).ShouldBe(0,
            customMessage: "no plane means no execution identity either — capture must not leave half of itself behind");
    }

    private async Task ExecuteAsync(Guid runId, IAgentHarness harness, bool withPlane = true)
    {
        using var scope = _fixture.BeginScope();
        var registry = new AgentHarnessRegistry(new[] { harness });
        var executor = new AgentRunExecutor(
            scope.Resolve<IAgentRunService>(),
            registry,
            new HarnessModelReconciler(registry, scope.Resolve<IModelPoolSelector>(), scope.Resolve<CodeSpaceDbContext>()),
            scope.Resolve<ISandboxRunnerRegistry>(),
            scope.Resolve<IAgentWorkspaceResolver>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.IModelCredentialResolver>(),
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
            nativeRecords: withPlane ? scope.Resolve<INativeRecordPlane>() : null);

        await executor.ExecuteAsync(runId, CancellationToken.None);
    }

    /// <summary>An opening whose worker fence cannot be this run's — the refusal 0137 raises on exactly the reclaim-for-reattach path the plane's own doc calls the intended outcome.</summary>
    private static NativeRecordCaptureRequest StaleFenceRequest(Guid teamId, Guid runId) => new()
    {
        TeamId = teamId,
        AgentRunId = runId,
        HarnessTypeKey = "scripted/v1",
        RunnerKind = "local",
        RunnerLocatorJson = "{\"spoolKey\":\"refused\"}",
        WorkerFenceEpoch = long.MaxValue,
        Channel = NativeRecordChannel.Stdout,
    };

    private async Task<Guid> CreateScriptedRunAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<IAgentRunService>().CreateAsync(
            new AgentTask { Goal = "scripted", Harness = "scripted", Model = "test-model", TimeoutSeconds = 1800 },
            teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None);
        return run.Id;
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"native-dual-{userId:N}@test.local", Name = $"native-dual-{userId:N}" });
        db.Team.Add(new Team { Id = teamId, Slug = $"native-dual-{teamId:N}", Name = "Native Dual Write Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    /// <summary>
    /// A scripted harness whose parser reproduces the two cases the normalized log cannot represent: a line it does not
    /// recognise (returns nothing, exactly as every real adapter does for an unknown native frame class) and a line it
    /// cannot read at all (throws, the bug case a durable capture floor has to survive).
    /// </summary>
    private sealed class SelectiveHarness : IAgentHarness
    {
        private readonly string _script;

        public SelectiveHarness(string script) => _script = script;

        public string Kind => "scripted";
        public string Version => "test";
        public IReadOnlyList<string> Models { get; } = new[] { "test-model" };

        public SandboxSpec BuildInvocation(AgentTask task) => new() { Command = "/bin/sh", Args = new[] { "-c", _script }, WorkingDirectory = task.WorkspaceDirectory, TimeoutSeconds = task.TimeoutSeconds };

        public IReadOnlyList<AgentEvent> ParseEvents(string rawLine)
        {
            if (rawLine.Trim() == "THROW") throw new InvalidOperationException("this native frame class is unreadable");
            if (string.IsNullOrWhiteSpace(rawLine) || rawLine.Trim() == "DROP") return Array.Empty<AgentEvent>();

            return new[] { new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = rawLine.Trim() } };
        }

        public IAgentEventFolder CreateFolder() => new TestEventFolder((fold, exitCode) =>
            exitCode == 0
                ? new AgentRunResult { Status = AgentRunStatus.Succeeded, ExitReason = "completed", Summary = fold.LastText }
                : new AgentRunResult { Status = AgentRunStatus.Failed, ExitReason = "non-zero-exit", Error = $"exit {exitCode}" });
    }
}
