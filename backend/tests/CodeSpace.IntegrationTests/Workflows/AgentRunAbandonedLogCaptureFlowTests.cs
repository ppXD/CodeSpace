using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// A run abandoned on a dead host used to claim, forever, that its logs were still finalizing.
///
/// <para>The log stream head is moved out of <see cref="AgentRunLogStreamState.Open"/> only by the LIVE worker doing
/// the capture. When that worker's host dies, the reconciler on some other worker terminalizes the RUN and touches no
/// stream, so the head kept sitting Open at a superseded fence and <c>RoomProjector.SummarizeLogs</c> folded it to
/// <see cref="RoomAgentLogStatus.Finalizing"/> — a progress report about a process that had stopped existing, with no
/// deadline and nothing that could ever move it.</para>
///
/// <para>Tier: high-fidelity Integration — the real <see cref="IAgentRunReconcilerService"/>, the real
/// <see cref="AgentRunLogService"/> and the real CAS runtime over real Postgres and a real on-disk provider root. The
/// durable prefix is written by the production append path and read back through the production read path, so
/// "the committed bytes survive the flip" is measured rather than asserted about columns.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunAbandonedLogCaptureFlowTests : IDisposable
{
    private const int SegmentBytes = 4096;
    private const int SegmentCount = 3;

    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];
    private readonly string? _previousHost;

    public AgentRunAbandonedLogCaptureFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _previousHost = Environment.GetEnvironmentVariable(LocalProcessRunner.SandboxHostEnvVar);
    }

    [Fact]
    public async Task An_abandoned_runs_open_stream_says_its_owner_is_gone_and_keeps_every_committed_byte()
    {
        var world = await StageRunCapturingOnAnotherHostAsync();

        await SweepFromTheSurvivingHostAsync(world);

        var stream = await StreamAsync(world);
        stream.State.ShouldBe(AgentRunLogStreamState.CaptureFailed,
            customMessage: "the capturing worker is gone, so Open is a claim nothing can ever make true — inspect agent_run_log_stream for this run");
        stream.ErrorCode.ShouldBe(AgentRunLogOwnerLossRequest.OwnerLostErrorCode);
        stream.CompletedAt.ShouldNotBeNull();

        stream.NextOffsetBytes.ShouldBe(SegmentBytes * SegmentCount, "the flip is a statement about the SOURCE, not about the bytes already committed");
        stream.TotalBytes.ShouldBe(SegmentBytes * SegmentCount);
        stream.WorkerFenceEpoch.ShouldBe(CaptureFence, "the stream still records WHICH generation captured it; the flip does not adopt the abandon's fence");

        // The durable prefix through the ordinary read path, not through the columns that describe it.
        using var scope = _fixture.BeginScope();
        var read = (await Logs(scope).ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, world.StreamId, 0, SegmentBytes * SegmentCount), CancellationToken.None))
            .ShouldBeOfType<AgentRunLogRangeResult.Available>();
        read.Bytes.ShouldBe(world.Bytes, "every segment the dead worker made durable must still read back byte-for-byte after the flip");
        (await scope.Resolve<CodeSpaceDbContext>().AgentRunLogSegment.AsNoTracking().CountAsync(row => row.StreamId == world.StreamId)).ShouldBe(SegmentCount);

        (await FoldAsync(world)).Status.ShouldBe(RoomAgentLogStatus.Incomplete,
            customMessage: "the Room must stop reporting progress for a capture that ended when its host did");
    }

    [Fact]
    public async Task The_abandoned_streams_error_message_cites_the_cleanup_receipts_that_abandon_wrote()
    {
        var world = await StageRunCapturingOnAnotherHostAsync();

        await SweepFromTheSurvivingHostAsync(world);

        var abandonEpoch = CaptureFence + 1;   // the abandon's own CAS bumps the fence by exactly one
        var message = (await StreamAsync(world)).ErrorMessage.ShouldNotBeNull();
        message.ShouldContain($"agent_run_id={world.AgentRunId}");
        message.ShouldContain($"fence_epoch={abandonEpoch}");

        using var scope = _fixture.BeginScope();
        var receipts = await scope.Resolve<IRunCleanupLedger>().ForRunsAsync(world.TeamId, [world.AgentRunId], CancellationToken.None);
        receipts.ShouldNotBeEmpty("the citation must point at rows that exist — an error message naming an empty query is worse than none");
        receipts.ShouldAllBe(receipt => receipt.FenceEpoch == abandonEpoch,
            customMessage: "the stream cites this exact generation, so every receipt of the same abandon must carry it");
    }

    [Fact]
    public async Task Without_the_owner_loss_statement_the_same_abandon_leaves_the_room_saying_finalizing()
    {
        var world = await StageRunCapturingOnAnotherHostAsync();

        // The mutation: the REAL reconciler, the real abandon, the real cleanup receipts — with the one new call
        // neutered. Everything else about the sweep is production code, so a green assertion here is a statement
        // about that call alone.
        await SweepFromTheSurvivingHostAsync(world, silenceOwnerLoss: true);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(row => row.Id == world.AgentRunId))
                .Status.ShouldBe(AgentRunStatus.Failed, "precondition: the abandon itself still ran");

        (await StreamAsync(world)).State.ShouldBe(AgentRunLogStreamState.Open,
            customMessage: "this is the defect: nothing else in the abandon path ever moves a log stream");
        (await FoldAsync(world)).Status.ShouldBe(RoomAgentLogStatus.Finalizing,
            customMessage: "and this is what the operator saw — a dead host's capture reported as still in progress");
    }

    [Fact]
    public async Task A_live_capture_is_never_flipped_by_an_owner_loss_statement()
    {
        var world = await StageLiveCaptureAsync();

        await SweepFromTheSurvivingHostAsync(world);
        (await StreamAsync(world)).State.ShouldBe(AgentRunLogStreamState.Open, "a run inside its liveness window is not a candidate, so the sweep states nothing about its capture");

        using var scope = _fixture.BeginScope();
        var logs = Logs(scope);

        (await logs.RecordOwnerLossAsync(new AgentRunLogOwnerLossRequest(world.TeamId, world.AgentRunId, CaptureFence, AgentRunLogOwnerLossRequest.OwnerLostErrorCode), CancellationToken.None))
            .ShouldBe(0, "the stream's fence is the run's current one — a live worker is still appending to it");

        (await logs.RecordOwnerLossAsync(new AgentRunLogOwnerLossRequest(world.TeamId, world.AgentRunId, CaptureFence - 1, AgentRunLogOwnerLossRequest.OwnerLostErrorCode), CancellationToken.None))
            .ShouldBe(0, "a caller the run has already moved past has no authority to state anything about it");

        (await StreamAsync(world)).State.ShouldBe(AgentRunLogStreamState.Open);
    }

    [Fact]
    public async Task A_superseded_generation_may_only_say_its_owner_is_gone_and_may_not_rewrite_the_bytes()
    {
        var world = await StageRunCapturingOnAnotherHostAsync();
        await SweepFromTheSurvivingHostAsync(world, silenceOwnerLoss: true);   // fence lapsed, stream still Open

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var rewritten = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE agent_run_log_stream SET state = 'CaptureFailed', error_code = 'capture.owner-lost', error_message = 'x',
                total_bytes = 0, next_offset_bytes = 0, segment_count = 0, next_segment_ordinal = 1,
                revision = revision + 1, completed_at = clock_timestamp(), last_modified_at = clock_timestamp()
            WHERE id = {world.StreamId}
            """, CancellationToken.None));
        rewritten.MessageText.ShouldContain("cannot rewrite its claim or byte head",
            customMessage: "widening the fence arm must not have loosened a single byte-head invariant beneath it");
    }

    [Theory]
    [InlineData("Truncated")]
    [InlineData("Unavailable")]
    [InlineData("Corrupt")]
    public async Task A_superseded_generation_may_not_make_any_other_terminal_claim(string terminalState)
    {
        var world = await StageRunCapturingOnAnotherHostAsync();
        await SweepFromTheSurvivingHostAsync(world, silenceOwnerLoss: true);   // fence lapsed, stream still Open

        using var scope = _fixture.BeginScope();
        var refused = await Should.ThrowAsync<PostgresException>(scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE agent_run_log_stream SET state = CAST({terminalState} AS VARCHAR(24)), error_code = 'capture.owner-lost', error_message = 'x',
                revision = revision + 1, completed_at = clock_timestamp(), last_modified_at = clock_timestamp()
            WHERE id = {world.StreamId}
            """, CancellationToken.None));

        refused.MessageText.ShouldContain("requires its current worker fence",
            customMessage: "each of these is a claim ABOUT THE BYTES, and a generation that never read them cannot make it");
    }

    /// <summary>
    /// The stream this migration had to be merged WITH, not around: a stall marker its dead producer left behind.
    ///
    /// <para>0232 supersedes the guard 0230 installed, so the two statements now live in one function body and the
    /// Room folds one row that can carry both facts. "Held; storage unavailable" is a promise that the bytes are
    /// still coming, and for a capture whose host is gone that promise is exactly the lie this whole change removes —
    /// so the terminal state has to outrank the marker, not be shadowed by it.</para>
    /// </summary>
    [Fact]
    public async Task An_owner_loss_flip_is_incomplete_even_when_the_stream_still_carries_a_stall_marker()
    {
        var world = await StageRunCapturingOnAnotherHostAsync();
        await MarkRemoteStalledAsync(world);

        await SweepFromTheSurvivingHostAsync(world);

        var stream = await StreamAsync(world);
        stream.State.ShouldBe(AgentRunLogStreamState.CaptureFailed,
            customMessage: "a producer that was waiting out an outage when its host died is still a producer that is gone");
        stream.RemoteStallSince.ShouldNotBeNull("the marker is the durable record of WHY the capture parked; the flip has no business erasing it");

        (await FoldAsync(world)).Status.ShouldBe(RoomAgentLogStatus.Incomplete,
            customMessage: "Stalled would tell the operator the bytes are queued behind an outage — there is no producer left to deliver them");
    }

    /// <summary>
    /// The half of the merged guard that is NOT this change's own: 0230's remote-stall arm, exercised against the
    /// function 0232 installs.
    ///
    /// <para>Both migrations redefine <c>agent_run_log_stream_guard()</c>, so only the last one to run survives. A
    /// suite that proved only the owner-loss transition would be just as green against a merged body that dropped
    /// this arm's untouched-column list — which is precisely how the collision would have destroyed work without a
    /// single red test. The admit path is proven by the production writer in AgentRunLogRemoteStallFlowTests; what
    /// needs saying here is that the refusal survived the merge too.</para>
    /// </summary>
    [Theory]
    [InlineData("total_bytes = total_bytes + 1")]
    [InlineData("next_offset_bytes = next_offset_bytes + 1")]
    public async Task A_health_write_that_smuggles_a_byte_head_change_is_still_refused(string smuggled)
    {
        var world = await StageLiveCaptureAsync();
        var sql = "UPDATE agent_run_log_stream SET remote_stall_since = now(), remote_stall_code = 'capture-backend-unavailable', "
            + smuggled + ", revision = revision + 1, last_modified_at = now() WHERE id = {0}";

        using var scope = _fixture.BeginScope();
        var refused = await Should.ThrowAsync<PostgresException>(
            scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(sql, [world.StreamId], CancellationToken.None));

        refused.MessageText.ShouldContain("remote-stall statement cannot rewrite its claim, byte head or terminal state",
            customMessage: "the fifth arm 0230 added must survive a migration that redefines the same function");

        var stream = await StreamAsync(world);
        stream.RemoteStallSince.ShouldBeNull("a refused statement leaves the row exactly as it was");
        stream.TotalBytes.ShouldBe(SegmentBytes * SegmentCount);
    }

    /// <summary>The dead producer's last health statement, written by the production writer while its fence was still the run's.</summary>
    private async Task MarkRemoteStalledAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var head = await Logs(scope).RecordRemoteStallAsync(new AgentRunLogRemoteStallRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = world.StreamId,
            WorkerFenceEpoch = CaptureFence, CaptureSessionId = world.CaptureSessionId,
            StalledSince = DateTimeOffset.UtcNow, StallCode = "capture-backend-unavailable",
        }, CancellationToken.None);

        head.ShouldNotBeNull("precondition: the marker has to be durable before the host goes away, or the test proves nothing about a stale one");
    }

    /// <summary>Stage a Running run whose capture is live and whose worker is still the current generation.</summary>
    private async Task<World> StageLiveCaptureAsync() => await StageAsync(handle: null, heartbeat: DateTimeOffset.UtcNow);

    /// <summary>Stage the exact state a killed pod leaves: a Running run past its lease, holding a handle minted on a host this process is not.</summary>
    private async Task<World> StageRunCapturingOnAnotherHostAsync()
    {
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
        var spool = NewRoot("spool");
        var handle = new SandboxHandle
        {
            Kind = "local", ProcessId = 2_000_000_000, LaunchHost = "host-dead-" + Guid.NewGuid().ToString("N"),
            SpoolDirectory = spool, Deadline = DateTimeOffset.UtcNow.AddMinutes(-1),
        };

        return await StageAsync(handle, stale);
    }

    private async Task<World> StageAsync(SandboxHandle? handle, DateTimeOffset heartbeat)
    {
        var teamId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"abandoned-capture-{actorId:N}@test.local", Name = "Abandoned Capture" });
        db.Team.Add(new Team { Id = teamId, Slug = $"abandoned-capture-{teamId:N}", Name = "Abandoned Capture", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        db.StorageProfile.Add(LocalProfile(teamId, actorId, profileId, NewRoot("cas"), now));
        await db.SaveChangesAsync();

        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = CaptureFence, StartedAt = heartbeat, HeartbeatAt = heartbeat, LeaseExpiresAt = heartbeat + AgentRunLiveness.Window,
            RunnerHandleJson = handle == null ? null : JsonSerializer.Serialize(handle, AgentJson.Options),
            CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();

        return new World(teamId, actorId, runId, await CaptureAsync(scope, teamId, runId, actorId, profileId, sessionId), sessionId);
    }

    /// <summary>Write the run's durable prefix through the production append path — real CAS objects on a real provider root.</summary>
    private async Task<Guid> CaptureAsync(ILifetimeScope scope, Guid teamId, Guid runId, Guid actorId, Guid profileId, Guid sessionId)
    {
        var logs = Logs(scope);
        var metadata = (await logs.OpenAsync(new AgentRunLogOpenRequest
        {
            TeamId = teamId, AgentRunId = runId, WorkerFenceEpoch = CaptureFence, CaptureSessionId = sessionId,
            StreamKind = AgentRunLogKinds.StandardOutput, ContentType = "text/plain", ContentEncoding = "utf-8", CaptureSource = "test-spool/v1",
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>().Metadata;

        for (var index = 0; index < SegmentCount; index++)
        {
            var segment = new byte[SegmentBytes];
            segment.AsSpan().Fill((byte)('a' + index));
            metadata = (await logs.AppendAsync(new AgentRunLogAppendRequest
            {
                TeamId = teamId, AgentRunId = runId, StreamId = metadata.StreamId, WorkerFenceEpoch = CaptureFence, CaptureSessionId = sessionId,
                ExpectedSegmentOrdinal = index + 1, ExpectedOffsetBytes = (long)index * SegmentBytes, ExpectedSourceOffsetBytes = (long)index * SegmentBytes,
                SourceLengthBytes = SegmentBytes, StorageProfileId = profileId, StorageProfileRevision = 1, ActorId = actorId, Bytes = segment,
            }, CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>().Metadata;
        }

        return metadata.StreamId;
    }

    /// <summary>Run the production sweep under a host identity that did not launch the run, optionally with the owner-loss statement silenced.</summary>
    private async Task SweepFromTheSurvivingHostAsync(World world, bool silenceOwnerLoss = false)
    {
        Environment.SetEnvironmentVariable(LocalProcessRunner.SandboxHostEnvVar, "host-alive-" + Guid.NewGuid().ToString("N"));

        using var scope = _fixture.BeginScope();
        var reconciler = silenceOwnerLoss ? Reconciler(scope, new SilentOwnerLossLogService(scope.Resolve<IAgentRunLogService>())) : scope.Resolve<IAgentRunReconcilerService>();
        await reconciler.ReconcileAsync(CancellationToken.None);
    }

    /// <summary>The container's own reconciler with exactly one collaborator replaced — the mutation's whole surface.</summary>
    private static IAgentRunReconcilerService Reconciler(ILifetimeScope scope, IAgentRunLogService logs) =>
        new AgentRunReconcilerService(scope.Resolve<CodeSpaceDbContext>(), scope.Resolve<IAgentRunService>(), scope.Resolve<IAgentRunCompletionNotifier>(),
            scope.Resolve<CodeSpace.Core.Services.Jobs.ICodeSpaceBackgroundJobClient>(), scope.Resolve<CodeSpace.Core.Services.Agents.Sandbox.ISandboxRunnerRegistry>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Mcp.IToolCallLedgerService>(), scope.Resolve<CodeSpace.Core.Services.Agents.Capture.ICaptureIntentService>(),
            scope.Resolve<CodeSpace.Core.Services.Agents.Capture.INativeRecordPlane>(), scope.Resolve<IRunCleanupLedger>(), logs,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRunReconcilerService>.Instance);

    /// <summary>The production reducer over the production projection — the same two lines <c>RoomProjector.AgentLogsAsync</c> runs.</summary>
    private async Task<RoomAgentLogSummary> FoldAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var rows = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogStream.AsNoTracking()
            .Where(stream => stream.TeamId == world.TeamId && stream.AgentRunId == world.AgentRunId)
            .Select(stream => new RoomProjector.AgentLogRow(stream.AgentRunId, stream.State, stream.SchemaVersion, stream.ManifestDigest != null, stream.RemoteStallSince != null))
            .ToListAsync();

        return RoomProjector.SummarizeLogs(rows);
    }

    private async Task<AgentRunLogStream> StreamAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRunLogStream.AsNoTracking().SingleAsync(row => row.Id == world.StreamId);
    }

    private static AgentRunLogService Logs(ILifetimeScope scope) =>
        new(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactCasRuntimeCoordinator>(), TimeProvider.System);

    private static StorageProfile LocalProfile(Guid teamId, Guid actorId, Guid profileId, string root, DateTimeOffset now)
    {
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"abandoned-capture-{profileId:N}", State = StorageProfileState.Active,
            CurrentRevision = 1, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        var config = JsonSerializer.SerializeToElement(new { rootPath = root });
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfigJson = config.GetRawText(), NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, config),
            CreatedDate = now, CreatedBy = actorId,
        });
        return profile;
    }

    private string NewRoot(string label)
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-abandoned-capture-{label}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    /// <summary>The generation that does the capturing; the abandon's CAS bumps the run past it by exactly one.</summary>
    private const long CaptureFence = 1;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LocalProcessRunner.SandboxHostEnvVar, _previousHost);
        foreach (var root in _roots)
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid AgentRunId, Guid StreamId, Guid CaptureSessionId)
    {
        public byte[] Bytes { get; init; } = Enumerable.Range(0, SegmentCount)
            .SelectMany(index => Enumerable.Repeat((byte)('a' + index), SegmentBytes)).ToArray();
    }

    /// <summary>Everything the real log seam does, except the one statement under test.</summary>
    private sealed class SilentOwnerLossLogService(IAgentRunLogService inner) : IAgentRunLogService
    {
        public Task<AgentRunLogOpenResult> OpenAsync(AgentRunLogOpenRequest request, CancellationToken cancellationToken) => inner.OpenAsync(request, cancellationToken);
        public Task<AgentRunLogAppendResult> AppendAsync(AgentRunLogAppendRequest request, CancellationToken cancellationToken) => inner.AppendAsync(request, cancellationToken);
        public Task<AgentRunLogFinalizeSourceResult> FinalizeSourceAsync(AgentRunLogFinalizeSourceRequest request, CancellationToken cancellationToken) => inner.FinalizeSourceAsync(request, cancellationToken);
        public Task<AgentRunLogCompleteResult> CompleteAsync(AgentRunLogCompleteRequest request, CancellationToken cancellationToken) => inner.CompleteAsync(request, cancellationToken);
        public Task<AgentRunLogFailCaptureResult> FailCaptureAsync(AgentRunLogFailCaptureRequest request, CancellationToken cancellationToken) => inner.FailCaptureAsync(request, cancellationToken);
        public Task<int> RecordOwnerLossAsync(AgentRunLogOwnerLossRequest request, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<AgentRunLogMetadataResult> GetMetadataAsync(Guid teamId, Guid streamId, CancellationToken cancellationToken) => inner.GetMetadataAsync(teamId, streamId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogMetadata>> ListMetadataAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListMetadataAsync(teamId, agentRunId, cancellationToken);
        public Task<IReadOnlyList<AgentRunLogCaptureHead>> ListCaptureHeadsAsync(Guid teamId, Guid agentRunId, CancellationToken cancellationToken) => inner.ListCaptureHeadsAsync(teamId, agentRunId, cancellationToken);
        public Task<AgentRunLogRangeResult> ReadRangeAsync(AgentRunLogRangeRequest request, CancellationToken cancellationToken) => inner.ReadRangeAsync(request, cancellationToken);
    }
}
