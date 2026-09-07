using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Credentials;
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
/// What an Agent Run log stream does when its team's STORAGE is the thing that is broken. The whole chain is real —
/// the real <see cref="IAgentRunLogService"/> over the real CAS runtime, the real driver broker, and a profile
/// revision whose credential reference dangles — so the classification under test is the one a deployment produces,
/// not one a fake asserts.
///
/// <para>Two properties, both of which a mis-classification silently breaks: a permanent storage fault terminalizes
/// the stream with a storage-shaped cause instead of being retried until the capture budget expires, and the
/// idempotency key that fault burned does not make that segment ordinal unwritable once the credential is repaired.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunLogStorageFaultFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];

    public AgentRunLogStorageFaultFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Final_source_read_obeys_retryability_and_reopens_the_same_durable_cursor_after_a_bounded_outage(int mode)
    {
        var world = await SeedWorldAsync();
        await RepairCredentialAsync(world);
        var runner = new LocalProcessRunner();
        var secret = $"redact-{Guid.NewGuid():N}";
        var stdout = $"before:{secret}:after";
        var stderr = $"stderr-{Guid.NewGuid():N}";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var handle = await runner.LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = ["-c", "printf '%s' \"$1\"; printf '%s' \"$2\" >&2", "_", stdout, stderr], WorkingDirectory = NewRoot(), TimeoutSeconds = 15 }, Guid.NewGuid().ToString("N"), deadline.Token);
        _roots.Add(handle.SpoolDirectory);
        var expected = await runner.AttachAsync(handle, (_, _) => Task.CompletedTask, deadline.Token);
        expected.Status.ShouldBe(SandboxStatus.Success);
        handle = handle with { AgentRunLogCaptureSessionId = Guid.NewGuid() };
        var source = new FinalReadFaultSource(runner, mode == 1 ? int.MaxValue : 1, mode != 2);
        var request = new AgentRunLogCaptureOpenRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, ActorId = world.ActorId, WorkerFenceEpoch = 7,
            Handle = handle, Source = source, Redactor = new SecretRedactor([secret]),
        };
        using var scope = _fixture.BeginScope();
        var logs = LogService(scope);
        var recovery = scope.Resolve<IAgentRunLogCaptureRecoveryService>();
        var bridge = new AgentRunLogCaptureBridge(logs, new StubStorageResolver(world.StorageProfileId), recovery,
            NullLogger<AgentRunLogCaptureBridge>.Instance, new AgentRunLogCaptureBridgeOptions(TimeSpan.FromSeconds(2), mode == 1 ? TimeSpan.FromMilliseconds(650) : TimeSpan.FromSeconds(8)));
        var capture = await bridge.OpenAsync(request, deadline.Token);

        (await capture.ObserveAsync((_, _) => Task.FromResult(expected), deadline.Token)).ShouldBeSameAs(expected);
        source.FailedOffsets.ShouldNotBeEmpty();
        source.FailedOffsets.ShouldAllBe(value => value == 0, "a failed source read cannot consume bytes or move the committed cursor");
        var head = (await logs.ListCaptureHeadsAsync(world.TeamId, world.AgentRunId, deadline.Token)).Single(value => value.Metadata.StreamKind == AgentRunLogKinds.StandardOutput);
        if (mode == 2)
        {
            head.Metadata.State.ShouldBe(AgentRunLogStreamState.CaptureFailed);
            head.Metadata.ErrorCode.ShouldBe("source-io-unavailable", "the problem's retryability governs recovery, not a guessed classification of the enum");
            head.CaptureFinalizedAt.ShouldBeNull();
            source.FailedOffsets.Count.ShouldBe(1);
            return;
        }
        if (mode == 1)
        {
            head.Metadata.State.ShouldBe(AgentRunLogStreamState.Open, "a bounded transient outage must leave the original stream writable for a later observer");
            head.CaptureFinalizedAt.ShouldBeNull();
            head.Metadata.SourceOffsetBytes.ShouldBe(0);
            // A new bridge and DB-facing service reopen the same capture identity. This is explicit same-host
            // reobservation, not a claim that the recurring recovery job already retrieves native source bytes.
            using var resumedScope = _fixture.BeginScope();
            var resumedLogs = LogService(resumedScope);
            var resumed = new AgentRunLogCaptureBridge(resumedLogs, new StubStorageResolver(world.StorageProfileId), resumedScope.Resolve<IAgentRunLogCaptureRecoveryService>(),
                NullLogger<AgentRunLogCaptureBridge>.Instance, new AgentRunLogCaptureBridgeOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(8)));
            var resumedCapture = await resumed.OpenAsync(request with { Source = runner }, deadline.Token);
            (await resumedCapture.ObserveAsync((_, _) => Task.FromResult(expected), deadline.Token)).ShouldBeSameAs(expected);
            (await resumedLogs.ListCaptureHeadsAsync(world.TeamId, world.AgentRunId, deadline.Token)).Single(value => value.Metadata.StreamKind == AgentRunLogKinds.StandardOutput).Metadata.StreamId.ShouldBe(head.Metadata.StreamId);
        }

        await bridge.CompleteRunAsync(world.TeamId, world.AgentRunId, 7, deadline.Token);
        var heads = await logs.ListCaptureHeadsAsync(world.TeamId, world.AgentRunId, deadline.Token);
        heads.Count.ShouldBe(2);
        heads.ShouldAllBe(value => value.Metadata.State == AgentRunLogStreamState.Completed && value.CaptureFinalizedAt != null);
        foreach (var item in heads)
        {
            var actual = (await logs.ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, item.Metadata.StreamId, 0, checked((int)item.Metadata.TotalBytes)), deadline.Token)).ShouldBeOfType<AgentRunLogRangeResult.Available>();
            var original = System.Text.Encoding.UTF8.GetBytes(item.Metadata.StreamKind == AgentRunLogKinds.StandardOutput ? stdout : stderr);
            actual.Bytes.ShouldBe(new SecretRedactor([secret]).CreateUtf8Stream().Transform(original, final: true).Bytes.ToArray());
            item.Metadata.SourceOffsetBytes.ShouldBe(original.LongLength);
        }
        var run = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(value => value.Id == world.AgentRunId, deadline.Token);
        run.Status.ShouldBe(AgentRunStatus.Running, "log capture must not claim responsibility for the task's execution outcome");
    }

    [Fact]
    public async Task An_unresolvable_storage_credential_terminalizes_the_stream_with_a_storage_cause_not_a_source_cause()
    {
        var world = await SeedWorldAsync();
        using var scope = _fixture.BeginScope();
        var logs = LogService(scope);
        var source = new StubLogSource(Enumerable.Repeat((byte)'c', 300 * 1024).ToArray());
        var bridge = new AgentRunLogCaptureBridge(logs, new StubStorageResolver(world.StorageProfileId), scope.Resolve<IAgentRunLogCaptureRecoveryService>(),
            NullLogger<AgentRunLogCaptureBridge>.Instance, new AgentRunLogCaptureBridgeOptions(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)));
        var expected = SandboxResultOf();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var session = await bridge.OpenAsync(OpenCapture(world, source), CancellationToken.None);
        var observed = await session.ObserveAsync((_, _) => Task.FromResult(expected), CancellationToken.None);

        observed.ShouldBeSameAs(expected, "shadow capture never reinterprets the harness result");
        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3),
            "a dangling credential is permanent; retrying it until the finalization budget cancels the capture leaves the stream Open for the reconciler to blame on the sandbox");
        var streams = await logs.ListMetadataAsync(world.TeamId, world.AgentRunId, CancellationToken.None);
        var stdout = streams.Single(value => value.StreamKind == AgentRunLogKinds.StandardOutput);
        stdout.State.ShouldBe(AgentRunLogStreamState.CaptureFailed);
        stdout.ErrorCode.ShouldBe("capture-storage-activation-failed",
            "the durable cause has to name the team's storage, or the operator is sent to look at the agent's log source");
    }

    [Fact]
    public async Task A_repaired_credential_lets_the_same_segment_ordinal_commit_under_the_next_generation()
    {
        // The permanent-wedge half. A non-retryable transfer records Failed against the intent for this exact
        // (stream, ordinal) key, and 0131_artifact_transfer_fence_claim.sql offers no route back out of Failed:
        // a fence claim raises 'terminal rows cannot be claimed' and a plain transition needs a lease a terminal row
        // may not hold. Creating the missing credential leaves storage_profile_revision untouched — that is what
        // makes this the poisoning case — so unless the repaired attempt claims a FRESH key, those bytes can never
        // be written under this stream again and the retry the append seam promises is a permanent rejection.
        var world = await SeedWorldAsync();
        var bytes = "log-bytes-that-must-survive-a-credential-repair"u8.ToArray();
        Guid streamId;
        var session = Guid.NewGuid();

        using (var brokenScope = _fixture.BeginScope())
        {
            var logs = LogService(brokenScope);
            streamId = (await logs.OpenAsync(Open(world, session), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>().Metadata.StreamId;
            var rejected = (await logs.AppendAsync(Append(world, streamId, session, bytes), CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Rejected>();

            rejected.Problem.IsRetryable.ShouldBeFalse("an unresolvable credential is not a retryable fault");
            await AssertIntentsAsync(world.TeamId, [($"agent-run-log/{streamId:N}/1", ArtifactTransferState.Failed)]);
        }

        await RepairCredentialAsync(world);

        using (var repairedScope = _fixture.BeginScope())
        {
            var logs = LogService(repairedScope);
            var appended = (await logs.AppendAsync(Append(world, streamId, session, bytes), CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
            appended.Metadata.TotalBytes.ShouldBe(bytes.Length);

            var read = (await logs.ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, streamId, 0, bytes.Length), CancellationToken.None)).ShouldBeOfType<AgentRunLogRangeResult.Available>();
            read.Bytes.ShouldBe(bytes);
        }

        await AssertIntentsAsync(world.TeamId, [
            ($"agent-run-log/{streamId:N}/1", ArtifactTransferState.Failed),
            ($"agent-run-log/{streamId:N}/1/g1", ArtifactTransferState.Committed),
        ]);
    }

    private static AgentRunLogService LogService(ILifetimeScope scope) =>
        new(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactCasRuntimeCoordinator>(), TimeProvider.System);

    /// <summary>Every intent this team owns, in key order, as (idempotency key, state) — so a test names the exact generations it expects and nothing else.</summary>
    private async Task AssertIntentsAsync(Guid teamId, (string Key, ArtifactTransferState State)[] expected)
    {
        using var scope = _fixture.BeginScope();
        var intents = await scope.Resolve<CodeSpaceDbContext>().ArtifactTransferIntent.AsNoTracking()
            .Where(value => value.TeamId == teamId).OrderBy(value => value.IdempotencyKey)
            .Select(value => new { value.IdempotencyKey, value.State }).ToListAsync();

        intents.Select(value => (value.IdempotencyKey, value.State)).ShouldBe(expected);
    }

    private static AgentRunLogOpenRequest Open(World world, Guid session) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, WorkerFenceEpoch = 7, CaptureSessionId = session,
        StreamKind = AgentRunLogKinds.StandardOutput, ContentType = "text/plain", ContentEncoding = "utf-8", CaptureSource = "test-capture/v1",
    };

    private static AgentRunLogAppendRequest Append(World world, Guid streamId, Guid session, byte[] bytes) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = streamId, WorkerFenceEpoch = 7,
        CaptureSessionId = session, ExpectedSegmentOrdinal = 1, ExpectedOffsetBytes = 0, ExpectedSourceOffsetBytes = 0,
        SourceLengthBytes = bytes.Length, StorageProfileId = world.StorageProfileId, StorageProfileRevision = 1,
        ActorId = world.ActorId, Bytes = bytes,
    };

    private static AgentRunLogCaptureOpenRequest OpenCapture(World world, StubLogSource source) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, ActorId = world.ActorId, WorkerFenceEpoch = 7,
        Handle = new SandboxHandle { Kind = "stub", ProcessId = 1, SpoolDirectory = "/opaque", Deadline = DateTimeOffset.MaxValue, AgentRunLogCaptureSessionId = Guid.NewGuid() },
        Source = source, Redactor = SecretRedactor.None,
    };

    private static SandboxResult SandboxResultOf() => new() { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "legacy", Stderr = "legacy-error" };

    /// <summary>
    /// A team whose log storage profile is Active and whose revision exists, but whose credential reference names a
    /// credential that is not there — the cheapest faithful stand-in for the deployment fault this suite is about,
    /// and one the local-rwx driver would otherwise serve happily.
    /// </summary>
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
        db.User.Add(new User { Id = actorId, Email = $"agent-log-fault-{actorId:N}@test.local", Name = "Agent Log Fault" });
        db.Team.Add(new Team { Id = teamId, Slug = $"agent-log-fault-{teamId:N}", Name = "Agent Log Fault", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"agent-log-fault-{profileId:N}", State = StorageProfileState.Active,
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
        return new World(teamId, actorId, profileId, credentialId, runId);
    }

    /// <summary>
    /// The repair: the credential the profile revision always pointed at now exists and is Active. The profile
    /// revision is not touched — which is exactly why the burned intent key cannot be escaped by a revision bump.
    /// </summary>
    private async Task RepairCredentialAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var credential = new StorageCredential
        {
            Id = world.CredentialId, TeamId = world.TeamId, StableName = $"agent-log-fault-{world.CredentialId:N}",
            CurrentRevision = 1, State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = world.ActorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, StorageCredentialId = world.CredentialId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt("{}"),
            SafeHint = "safe", EnvelopeFingerprint = $"sha256:{new string('b', 64)}", CreatedDate = now, CreatedBy = world.ActorId,
        });
        db.StorageCredential.Add(credential);
        await db.SaveChangesAsync();
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-agent-log-fault-{Guid.NewGuid():N}");
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

    private sealed record World(Guid TeamId, Guid ActorId, Guid StorageProfileId, Guid CredentialId, Guid AgentRunId);

    private sealed class FinalReadFaultSource(ISandboxDurableLogSource inner, int failures, bool retryable) : ISandboxDurableLogSource
    {
        private int _remaining = failures;
        public List<long> FailedOffsets { get; } = [];
        public IReadOnlyList<SandboxDurableLogDescriptor> DescribeLogs(SandboxHandle handle) => inner.DescribeLogs(handle);
        public Task<SandboxDurableLogReadResult> ReadAsync(SandboxDurableLogReadRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!request.FinalDrain) return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.NoData());
            if (request.SourceKey == "stdout" && _remaining-- > 0)
            {
                FailedOffsets.Add(request.OffsetBytes);
                return Task.FromResult<SandboxDurableLogReadResult>(new SandboxDurableLogReadResult.Unavailable(new SandboxDurableLogProblem(SandboxDurableLogProblemCode.IoUnavailable, retryable)));
            }
            return inner.ReadAsync(request, cancellationToken);
        }
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
