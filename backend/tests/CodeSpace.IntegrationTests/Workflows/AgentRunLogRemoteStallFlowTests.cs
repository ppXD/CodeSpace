using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// What PostgreSQL actually holds while a log stream waits out a transient storage outage, and what it holds after the
/// outage clears. The whole write path is real — the real <see cref="AgentRunLogService"/> over the real CAS runtime
/// and the real local-rwx driver — with only the CAS verdict swapped for the one a 503 produces, so the stall the
/// columns record is the one the production mapping reaches (<c>ProviderUnavailableTransient</c> →
/// <c>BackendUnavailable</c>, retryable).
///
/// <para>Two properties the schema alone cannot show: the stall marker is written under the producer's exact fence and
/// then CLEARED once a segment commits (a marker nobody clears makes a live run read as permanently broken), and the
/// recovered stream finalizes with a complete v3 manifest over every byte — so the wait cost the run nothing. The Room
/// fold is asserted over those same real rows rather than a hand-built one.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunLogRemoteStallFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public AgentRunLogRemoteStallFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_transient_outage_records_a_stall_holds_every_byte_and_clears_the_marker_when_it_recovers()
    {
        var world = await SeedWorldAsync();
        var stdout = Payload(300 * 1024 + 37);
        var source = new StubLogSource(stdout);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var scope = _fixture.BeginScope();
        var outage = new TransientOutageCoordinator(scope.Resolve<IArtifactCasRuntimeCoordinator>()) { Unavailable = true };
        var logs = new AgentRunLogService(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), outage, TimeProvider.System);
        var bridge = new AgentRunLogCaptureBridge(logs, new StubStorageResolver(world.StorageProfileId), scope.Resolve<IAgentRunLogCaptureRecoveryService>(),
            NullLogger<AgentRunLogCaptureBridge>.Instance, new AgentRunLogCaptureBridgeOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20)), logs);
        var expected = SandboxResultOf();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = await bridge.OpenAsync(OpenCapture(world, source), deadline.Token);
        var observing = capture.ObserveAsync(async (_, _) => { await release.Task; return expected; }, deadline.Token);

        await WaitAsync(async () => (await StreamAsync(world, deadline.Token)).RemoteStallSince != null, "the stall marker never reached agent_run_log_stream", deadline.Token);
        var stalled = await StreamAsync(world, deadline.Token);
        stalled.RemoteStallCode.ShouldBe("capture-backend-unavailable", "the durable marker names the refusal being waited out, in the bridge's own vocabulary");
        stalled.State.ShouldBe(AgentRunLogStreamState.Open, "a retryable outage is not a terminal capture verdict");
        stalled.SegmentCount.ShouldBe(0);
        stalled.TotalBytes.ShouldBe(0);
        stalled.SourceOffsetBytes.ShouldBe(0, "no offset may advance while the segment is still queued");
        stalled.CompletedAt.ShouldBeNull();
        stalled.ErrorCode.ShouldBeNull();
        RoomProjector.SummarizeLogs([Row(stalled)]).Status.ShouldBe(RoomAgentLogStatus.Stalled, "the Room must stop reading \"Finalizing\" through a storage incident");

        // The real clock here, deliberately: one production backoff (5s base, jittered down) is inside this test's
        // budget, and spending it proves the retry schedule itself rather than a virtual stand-in for it.
        outage.Unavailable = false;
        await WaitAsync(async () => (await StreamAsync(world, deadline.Token)).RemoteStallSince == null, "the stall marker was never cleared after the provider recovered", deadline.Token);
        release.TrySetResult();
        (await observing).ShouldBeSameAs(expected);
        await bridge.CompleteRunAsync(world.TeamId, world.AgentRunId, 7, deadline.Token);

        var recovered = await StreamAsync(world, deadline.Token);
        recovered.State.ShouldBe(AgentRunLogStreamState.Completed, "an outage that cleared inside the ceiling costs the run nothing");
        recovered.RemoteStallSince.ShouldBeNull();
        recovered.RemoteStallCode.ShouldBeNull();
        recovered.ManifestDigest.ShouldNotBeNull("a v3 stream that completes carries the manifest digest its integrity proof is made of");
        recovered.TotalBytes.ShouldBe(stdout.LongLength);
        recovered.SourceOffsetBytes.ShouldBe(stdout.LongLength);
        outage.Refusals.ShouldBeGreaterThan(0, "the test proves nothing unless the provider actually refused at least once");

        var read = (await logs.ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, recovered.Id, 0, checked((int)recovered.TotalBytes)), deadline.Token)).ShouldBeOfType<AgentRunLogRangeResult.Available>();
        read.Bytes.ShouldBe(stdout, "every held byte has to come back, in order — this is the assertion the old drop-on-transient path could not pass");
        RoomProjector.SummarizeLogs([Row(recovered)]).Status.ShouldBe(RoomAgentLogStatus.Verified);
    }

    [Fact]
    public async Task An_outage_past_the_park_ceiling_terminalizes_the_stream_and_files_a_real_RemoteUnavailable_gap()
    {
        // The park path's two durable writes, against the guards that actually admit or refuse them: the terminal
        // transition on a row whose stall columns are set, and 0231's widened reason CHECK. A CHECK pinned only as SQL
        // text and an EF mirror is a CHECK no production INSERT has ever been offered to.
        var world = await SeedWorldAsync();
        var stdout = Payload(300 * 1024 + 11);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var scope = _fixture.BeginScope();
        var outage = new TransientOutageCoordinator(scope.Resolve<IArtifactCasRuntimeCoordinator>()) { Unavailable = true };
        var logs = new AgentRunLogService(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), outage, TimeProvider.System);
        var backpressure = new CaptureBackpressureOptions
        {
            RetryBase = TimeSpan.FromMilliseconds(200), RetryCeiling = TimeSpan.FromMilliseconds(400), ParkAfter = TimeSpan.FromMilliseconds(1),
        };
        var bridge = new AgentRunLogCaptureBridge(logs, new StubStorageResolver(world.StorageProfileId), scope.Resolve<IAgentRunLogCaptureRecoveryService>(),
            NullLogger<AgentRunLogCaptureBridge>.Instance, new AgentRunLogCaptureBridgeOptions(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)) { Backpressure = backpressure },
            logs, scope.Resolve<IRunDataCompletenessWriter>());
        var expected = SandboxResultOf();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var capture = await bridge.OpenAsync(OpenCapture(world, new StubLogSource(stdout)), deadline.Token);
        var observing = capture.ObserveAsync(async (_, _) => { await release.Task; return expected; }, deadline.Token);
        await WaitAsync(async () => (await StreamAsync(world, deadline.Token)).State != AgentRunLogStreamState.Open, "the stream never parked past its ceiling", deadline.Token);
        release.TrySetResult();
        (await observing).ShouldBeSameAs(expected);

        var parked = await StreamAsync(world, deadline.Token);
        parked.State.ShouldBe(AgentRunLogStreamState.CaptureFailed);
        parked.ErrorCode.ShouldBe(CaptureBackpressureOptions.RemoteOutageExhaustedCode);
        parked.CompletedAt.ShouldNotBeNull();
        parked.TotalBytes.ShouldBe(0, "the byte head cannot move on the way to a park");
        parked.RemoteStallSince.ShouldNotBeNull("the marker stays as the durable evidence of WHY this stream parked");

        using var reading = _fixture.BeginScope();
        var gap = await reading.Resolve<CodeSpaceDbContext>().WorkflowRunCaptureGap.AsNoTracking()
            .SingleAsync(value => value.TeamId == world.TeamId && value.AgentRunId == world.AgentRunId, deadline.Token);
        gap.Reason.ShouldBe(CaptureGapReason.RemoteUnavailable, "0231's widened CHECK has to accept the value the producer actually writes");
        gap.WorkflowRunId.ShouldBeNull("a standalone Agent Run has no workflow run, and demanding one is what made its gap unrepresentable");
        gap.StreamId.ShouldBe(parked.Id);
        gap.RangeKind.ShouldBe(CaptureGapRangeKind.ByteOffset);
        gap.RangeStart.ShouldBe(0);
        gap.RangeEnd.ShouldBeNull();
        gap.Resolution.ShouldBe(CaptureGapResolution.Open);
        gap.ReasonDetail.ShouldContain("capture-backend-unavailable");
    }

    [Fact]
    public void The_container_supplies_both_health_planes_to_the_capture_bridge()
    {
        // These are optional constructor parameters (the blessed shape for a plane a focused test construction omits),
        // and an optional parameter the container quietly leaves at null is a capability with no caller: the outage
        // would be held exactly as designed and neither the Room nor the completeness plane would ever hear about it.
        // So the wiring is asserted, not assumed.
        using var scope = _fixture.BeginScope();
        var bridge = scope.Resolve<IAgentRunLogCaptureBridge>().ShouldBeOfType<AgentRunLogCaptureBridge>();

        Field(bridge, "_stalls").ShouldNotBeNull("nothing would write remote_stall_since, so the Room would read \"Finalizing\" through every storage incident");
        Field(bridge, "_completeness").ShouldNotBeNull("a parked stream's lost span would go unrecorded, and the run would report a complete record it does not have");
        Field(bridge, "_clock").ShouldNotBeNull();
        scope.Resolve<IAgentRunLogRemoteStallWriter>().ShouldBeOfType<AgentRunLogService>();
    }

    private static object? Field(AgentRunLogCaptureBridge bridge, string name) =>
        typeof(AgentRunLogCaptureBridge).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bridge);

    private static RoomProjector.AgentLogRow Row(AgentRunLogStream stream) =>
        new(stream.AgentRunId, stream.State, stream.SchemaVersion, stream.ManifestDigest != null, stream.RemoteStallSince != null);

    private async Task<AgentRunLogStream> StreamAsync(World world, CancellationToken cancellationToken)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRunLogStream.AsNoTracking()
            .SingleAsync(value => value.TeamId == world.TeamId && value.AgentRunId == world.AgentRunId && value.StreamKind == AgentRunLogKinds.StandardOutput, cancellationToken);
    }

    /// <summary>Explicit timeout naming the watched signal AND how to look at it by hand (Rule 12.10).</summary>
    private static async Task WaitAsync(Func<Task<bool>> condition, string signal, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(40))
        {
            if (await condition()) return;
            await Task.Delay(50, cancellationToken);
        }
        throw new Xunit.Sdk.XunitException($"{signal} — inspect with: SELECT state, segment_count, source_offset_bytes, remote_stall_since, remote_stall_code FROM agent_run_log_stream");
    }

    private static byte[] Payload(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++) bytes[index] = (byte)('a' + index % 23);
        return bytes;
    }

    private static AgentRunLogCaptureOpenRequest OpenCapture(World world, StubLogSource source) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, ActorId = world.ActorId, WorkerFenceEpoch = 7,
        Handle = new SandboxHandle { Kind = "stub", ProcessId = 1, SpoolDirectory = "/opaque", Deadline = DateTimeOffset.MaxValue, AgentRunLogCaptureSessionId = Guid.NewGuid() },
        Source = source, Redactor = SecretRedactor.None,
    };

    private static SandboxResult SandboxResultOf() => new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "legacy", Stderr = "legacy-error" };

    /// <summary>A team whose log storage route is Active, whose credential resolves, and whose local-rwx root is real — so the ONLY fault in play is the one the decorator injects.</summary>
    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var root = NewRoot();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"agent-log-stall-{actorId:N}@test.local", Name = "Agent Log Stall" });
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-log-stall-{teamId:N}", Name = "Agent Log Stall", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var credential = new StorageCredential
        {
            Id = credentialId, TeamId = teamId, StableName = $"agent-log-stall-{credentialId:N}",
            CurrentRevision = 1, State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = actorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credentialId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt("{}"),
            SafeHint = "safe", EnvelopeFingerprint = $"sha256:{new string('b', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageCredential.Add(credential);
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"agent-log-stall-{profileId:N}", State = StorageProfileState.Active,
            CurrentRevision = 1, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = $"{{\"rootPath\":\"{root.Replace("\\", "\\\\")}\"}}",
            CredentialRef = $"db:{credentialId:D}:1", NamespaceFingerprint = $"sha256:{new string('a', 64)}",
            CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        var runId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = 7, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();
        return new World(teamId, actorId, profileId, runId);
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-agent-log-stall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    public void Dispose()
    {
        foreach (var root in _roots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid StorageProfileId, Guid AgentRunId);

    /// <summary>The provider being out: the exact verdict the CAS layer produces for a 503, delegating to the real runtime the moment it clears.</summary>
    private sealed class TransientOutageCoordinator(IArtifactCasRuntimeCoordinator inner) : IArtifactCasRuntimeCoordinator
    {
        public volatile bool Unavailable;
        private int _refusals;

        public int Refusals => Volatile.Read(ref _refusals);

        public Task<ArtifactCasTransferResult> PutAsync(ArtifactCasTransferRequest request, CancellationToken cancellationToken)
        {
            if (!Unavailable) return inner.PutAsync(request, cancellationToken);
            Interlocked.Increment(ref _refusals);
            return Task.FromResult<ArtifactCasTransferResult>(new ArtifactCasTransferResult.Rejected(null, new ArtifactCasProblem(ArtifactCasProblemCode.ProviderUnavailableTransient, true)));
        }

        public Task<ArtifactCasReadResult> OpenReadAsync(ArtifactCasReadRequest request, CancellationToken cancellationToken) => inner.OpenReadAsync(request, cancellationToken);
    }

    private sealed class StubStorageResolver(Guid profileId) : IAgentRunLogStorageResolver
    {
        public Task<AgentRunLogStorageResolution> ResolveAsync(Guid teamId, CancellationToken cancellationToken) =>
            Task.FromResult<AgentRunLogStorageResolution>(new AgentRunLogStorageResolution.Ready(profileId, 1));
    }

    private sealed class StubLogSource(byte[] stdout) : ISandboxDurableLogSource
    {
        public IReadOnlyList<SandboxDurableLogDescriptor> DescribeLogs(SandboxHandle handle) =>
        [
            new("stdout", AgentRunLogKinds.StandardOutput, AgentRunLogRepresentations.PlainTextContentType, AgentRunLogRepresentations.Utf8ContentEncoding, "stub-spool/v1"),
        ];

        public Task<SandboxDurableLogReadResult> ReadAsync(SandboxDurableLogReadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = stdout.LongLength - request.OffsetBytes;
            if (available == 0 && request.FinalDrain) return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.EndOfSource(false));
            if (available == 0 || (!request.FinalDrain && available < request.MinimumBytes)) return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.NoData());
            var length = (int)Math.Min(available, request.MaximumBytes);
            return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.Available(stdout.AsMemory((int)request.OffsetBytes, length)));
        }
    }
}
