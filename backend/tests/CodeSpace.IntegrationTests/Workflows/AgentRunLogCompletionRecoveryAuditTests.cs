using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using CodeSpace.StorageTestWorker;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Test-first audit, intentionally red until resumable completion exists. PostgreSQL, immutable CAS objects, the
/// runtime/driver and EOF verification are real. Faults occur at a counted boundary after two complete segment
/// reads, rather than assuming a machine must hash some number of bytes within a particular wall-clock duration.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
[Trait("Audit", "LogCompletionResume")]
public sealed partial class AgentRunLogCompletionRecoveryAuditTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int SegmentBytes = 1024 * 1024;
    private const int SegmentCount = 4;
    private readonly List<string> _roots = [];
    // Recovery scans the whole deployment. Each case needs its own real database, so another test's abandoned
    // terminal intent cannot be mistaken for this case's claimed attempt or exhaust its counted step budget.
    private readonly PostgresFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();

    [Fact]
    public async Task Uninterrupted_v3_completion_verifies_all_content_with_an_explicit_manifest_identity()
    {
        var world = await SeedAsync(declareRecovery: false);
        using var scope = fixture.BeginScope();
        var probe = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>());
        var completed = (await Logs(scope, probe).CompleteAsync(world.Complete, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();

        completed.Metadata.State.ShouldBe(AgentRunLogStreamState.Completed);
        AssertManifest(world, completed.Metadata);
        AssertReads(probe.Reads.ToArray(), world.ObjectIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_completion_must_resume_without_rereading_verified_prefix(bool killProcess)
    {
        var world = await SeedAsync(declareRecovery: false);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        if (killProcess) await KillAfterPrefixAsync(world, deadline.Token);
        else await CancelAfterPrefixAsync(world, deadline.Token);
        await AssertUncommittedHeadAsync(world);

        // A fresh service and container scope are mandatory: an in-memory cache could mask the crash defect.
        using var resumedScope = fixture.BeginScope();
        var resumed = new LogCompletionReadProbe(resumedScope.Resolve<IArtifactCasRuntimeCoordinator>());
        var completed = (await Logs(resumedScope, resumed).CompleteAsync(world.Complete, deadline.Token)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        AssertManifest(world, completed.Metadata);
        var reads = resumed.Reads.ToArray();
        output.WriteLine($"mode={(killProcess ? "SIGKILL" : "cancellation")}; retry read ordinals={Ordinals(world, reads)}; retry bytes={reads.Sum(value => value.BytesRead)}; prefix verified before interruption=1,2");
        reads.ShouldAllBe(value => value.MaximumRequestedBytes <= 128 * 1024 && value.EofCount == 1 && value.Disposed);

        // The new durability invariant. At aafed752 this is exactly 1,2,3,4, with another complete 4 MiB read.
        reads.Select(value => value.ArtifactObjectId).ShouldBe(world.ObjectIds.Skip(2), "a completed prefix must survive cancellation/SIGKILL rather than be verified from ordinal 1 again");
    }

    [Fact]
    public async Task Bounded_recovery_must_retain_verified_progress_instead_of_exhausting_on_the_same_prefix()
    {
        var world = await SeedAsync(declareRecovery: true);
        await MarkTerminalAsync(world);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        AgentRunLogCaptureIntent? finalIntent = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var scope = fixture.BeginScope();
            var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var probe = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>(), async token =>
            {
                blocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
            var recovery = Recovery(scope, Logs(scope, probe));
            AgentRunLogCaptureRecoverySummary summary;
            do
            {
                summary = await recovery.ReconcileAsync(deadline.Token);
                if (summary.Claimed == 0) await Task.Delay(10, deadline.Token);
            } while (summary.Claimed == 0);
            summary.Claimed.ShouldBe(1);
            summary.LostLease.ShouldBe(0);
            finalIntent = await IntentAsync(world);
            finalIntent.RecoveryAttemptCount.ShouldBe(attempt);
            output.WriteLine($"attempt={attempt}; read ordinals={Ordinals(world, probe.Reads.ToArray())}; bytes={probe.Reads.Sum(value => value.BytesRead)}; intent={finalIntent.State}; cause={finalIntent.LastErrorCode}");
            probe.Reads.Count.ShouldBeLessThanOrEqualTo(2);
            probe.Reads.ShouldAllBe(value => value.BytesRead == SegmentBytes && value.EofCount == 1 && value.Disposed && value.MaximumRequestedBytes <= 128 * 1024);
            if (summary.Completed == 1)
            {
                finalIntent.State.ShouldBe(AgentRunLogCaptureIntentState.Completed);
                break;
            }
            blocked.Task.IsCompletedSuccessfully.ShouldBeTrue("the real reader must have reached the counted prefix barrier before the operation timer fired");
            await AssertUncommittedHeadAsync(world);
        }
        if (finalIntent!.State != AgentRunLogCaptureIntentState.Completed)
        {
            finalIntent.LastErrorCode.ShouldBe("recovery-exhausted");
            finalIntent.LastErrorMessage.ShouldContain("recovery-operation-timeout");
            // Read the SAME recorded physical objects without the artificial slow-read barrier. Their actual whole
            // SHA succeeds, proving exhaustion was loss of progress rather than missing/corrupt CAS content.
            using var healthyScope = fixture.BeginScope();
            var healthy = new LogCompletionReadProbe(healthyScope.Resolve<IArtifactCasRuntimeCoordinator>());
            var completed = (await Logs(healthyScope, healthy).CompleteAsync(world.Complete, deadline.Token)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
            AssertManifest(world, completed.Metadata);
            AssertReads(healthy.Reads.ToArray(), world.ObjectIds);
        }

        finalIntent.State.ShouldBe(AgentRunLogCaptureIntentState.Completed, "each bounded attempt verified two complete segments, yet none of that progress survived to let the next attempt start at the suffix");
    }

    private async Task CancelAfterPrefixAsync(World world, CancellationToken deadline)
    {
        using var scope = fixture.BeginScope();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(deadline);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>(), async token =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var completing = Logs(scope, probe).CompleteAsync(world.Complete, cancelled.Token);
        await reached.Task.WaitAsync(deadline);
        AssertReads(probe.Reads.ToArray(), world.ObjectIds.Take(2));
        cancelled.Cancel();
        try { await completing; throw new Xunit.Sdk.XunitException("CompleteAsync ignored cancellation at the counted read boundary."); }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
        output.WriteLine("cancelled only after 2 real CAS streams reached verified EOF and were disposed");
    }

    private async Task KillAfterPrefixAsync(World world, CancellationToken deadline)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "exec", "--runtimeconfig", Path.Combine(AppContext.BaseDirectory, "CodeSpace.IntegrationTests.runtimeconfig.json"), "--depsfile", Path.Combine(AppContext.BaseDirectory, "CodeSpace.IntegrationTests.deps.json"), typeof(LogCompletionAuditWorker).Assembly.Location, "log-completion-audit", JsonSerializer.Serialize(world.Complete) }) start.ArgumentList.Add(argument);
        start.Environment[LogCompletionAuditWorker.ConnectionEnvironmentVariable] = fixture.ConnectionString;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the killable completion consumer.");
        var errors = process.StandardError.ReadToEndAsync(deadline);
        try
        {
            string? barrier;
            do
            {
                barrier = await process.StandardOutput.ReadLineAsync(deadline);
                if (barrier == null) throw new Xunit.Sdk.XunitException($"Completion child exited before the barrier: {await errors}");
            } while (!barrier.StartsWith("verified-prefix:", StringComparison.Ordinal));
            var reads = JsonSerializer.Deserialize<LogSegmentReadObservation[]>(barrier["verified-prefix:".Length..])!;
            AssertReads(reads, world.ObjectIds.Take(2));
            process.HasExited.ShouldBeFalse();
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(deadline);
            process.ExitCode.ShouldBe(137, "this is an actual Unix SIGKILL, not graceful cancellation or an injected exception");
            output.WriteLine($"SIGKILL exit={process.ExitCode}; child prefix ordinals={Ordinals(world, reads)}; bytes={reads.Sum(value => value.BytesRead)}");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private async Task<World> SeedAsync(bool declareRecovery, int segmentCount = SegmentCount, int segmentBytes = SegmentBytes, LegacyLogDatabase? legacy = null)
    {
        var teamId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var root = Path.Combine(Path.GetTempPath(), $"codespace-log-completion-audit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        using var scope = legacy?.BeginScope() ?? fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"log-completion-{actorId:N}@test.local", Name = "Log Completion Audit" });
        db.Team.Add(new Team { Id = teamId, Slug = $"log-completion-{teamId:N}", Name = "Log Completion Audit", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var profile = new StorageProfile { Id = profileId, TeamId = teamId, StableName = $"log-audit-{profileId:N}", State = StorageProfileState.Active, CurrentRevision = 1, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId };
        var config = JsonSerializer.SerializeToElement(new { rootPath = root });
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1, ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey,
            NonSecretConfigJson = config.GetRawText(), NamespaceFingerprint = StorageProfileRules.NamespaceFingerprint(LocalRwxArtifactStorageDriverFactory.TypeKey, config), CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}", FenceEpoch = 7, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId });
        await db.SaveChangesAsync();
        if (legacy != null) await legacy.InsertStreamAndUpgradeAsync(new AgentRunLogOpenRequest { TeamId = teamId, AgentRunId = runId, WorkerFenceEpoch = 7, CaptureSessionId = sessionId, StreamKind = AgentRunLogKinds.StandardOutput, ContentType = "text/plain", ContentEncoding = "utf-8", CaptureSource = "test-spool/v1" });
        var logs = Logs(scope, scope.Resolve<IArtifactCasRuntimeCoordinator>());
        if (declareRecovery)
        {
            (await Recovery(scope, logs).DeclareAsync(new AgentRunLogCaptureDeclarationRequest
            {
                TeamId = teamId, AgentRunId = runId, WorkerFenceEpoch = 7, CaptureSessionId = sessionId,
                Streams = [new AgentRunLogExpectedStream(AgentRunLogKinds.StandardOutput, "text/plain", "utf-8", "test-spool/v1")],
            }, CancellationToken.None)).ShouldBeOfType<AgentRunLogCaptureDeclarationResult.Declared>();
        }
        var metadata = (await logs.OpenAsync(new AgentRunLogOpenRequest { TeamId = teamId, AgentRunId = runId, WorkerFenceEpoch = 7, CaptureSessionId = sessionId, StreamKind = AgentRunLogKinds.StandardOutput, ContentType = "text/plain", ContentEncoding = "utf-8", CaptureSource = "test-spool/v1" }, CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>().Metadata;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var segment = new byte[segmentBytes];
        var objects = new List<Guid>();
        for (var index = 0; index < segmentCount; index++)
        {
            segment.AsSpan().Fill((byte)('a' + index));
            hash.AppendData(segment);
            var appended = (await logs.AppendAsync(new AgentRunLogAppendRequest
            {
                TeamId = teamId, AgentRunId = runId, StreamId = metadata.StreamId, WorkerFenceEpoch = 7, CaptureSessionId = sessionId,
                ExpectedSegmentOrdinal = index + 1, ExpectedOffsetBytes = (long)index * segmentBytes, ExpectedSourceOffsetBytes = (long)index * segmentBytes, SourceLengthBytes = segmentBytes,
                StorageProfileId = profileId, StorageProfileRevision = 1, ActorId = actorId, Bytes = segment,
            }, CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>();
            metadata = appended.Metadata;
            objects.Add(appended.Segment.ArtifactObjectId);
        }
        metadata = (await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest { TeamId = teamId, AgentRunId = runId, StreamId = metadata.StreamId, WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = metadata.Revision, ExpectedSourceOffsetBytes = (long)segmentBytes * segmentCount }, CancellationToken.None)).ShouldBeOfType<AgentRunLogFinalizeSourceResult.Finalized>().Metadata;
        return new World(new AgentRunLogCompleteRequest { TeamId = teamId, AgentRunId = runId, StreamId = metadata.StreamId, WorkerFenceEpoch = 7, CaptureSessionId = sessionId, ExpectedRevision = metadata.Revision }, objects.ToArray(), Convert.ToHexStringLower(hash.GetHashAndReset()), segmentBytes, segmentCount);
    }

    private async Task AssertUncommittedHeadAsync(World world)
    {
        using var scope = fixture.BeginScope();
        var head = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogStream.AsNoTracking().SingleAsync(value => value.TeamId == world.Complete.TeamId && value.Id == world.Complete.StreamId);
        head.State.ShouldBe(AgentRunLogStreamState.Open);
        head.Revision.ShouldBe(world.Complete.ExpectedRevision);
        head.ContentDigest.ShouldBeNull();
        head.ContentDigestAlgorithm.ShouldBeNull();
        head.CaptureFinalizedAt.ShouldNotBeNull();
        head.TotalBytes.ShouldBe(SegmentBytes * SegmentCount);
    }

    private async Task MarkTerminalAsync(World world)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.AgentRun.SingleAsync(value => value.Id == world.Complete.AgentRunId);
        run.Status = AgentRunStatus.Succeeded;
        run.ResultJson = "{\"status\":\"Succeeded\"}";
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<AgentRunLogCaptureIntent> IntentAsync(World world)
    {
        using var scope = fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().AgentRunLogCaptureIntent.AsNoTracking().SingleAsync(value => value.TeamId == world.Complete.TeamId && value.AgentRunId == world.Complete.AgentRunId);
    }

    private static AgentRunLogService Logs(ILifetimeScope scope, IArtifactCasRuntimeCoordinator cas) => new(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), cas, TimeProvider.System);
    private static AgentRunLogCaptureRecoveryService Recovery(ILifetimeScope scope, IAgentRunLogService logs) => new(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), logs,
        new AgentRunLogCaptureRecoveryOptions(1, 1, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), new AgentRunLogCaptureRetryPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 3, TimeSpan.FromMinutes(5), TimeSpan.Zero)));
    private static string Ordinals(World world, IReadOnlyList<LogSegmentReadObservation> reads) => string.Join(",", reads.Select(value => Array.IndexOf(world.ObjectIds, value.ArtifactObjectId) + 1));
    private static void AssertReads(IReadOnlyList<LogSegmentReadObservation> reads, IEnumerable<Guid> expectedObjects)
    {
        reads.Select(value => value.ArtifactObjectId).ShouldBe(expectedObjects);
        reads.ShouldAllBe(value => value.BytesRead == SegmentBytes && value.EofCount == 1 && value.Disposed && value.MaximumRequestedBytes <= 128 * 1024);
    }
    public async Task DisposeAsync()
    {
        try { await fixture.DisposeAsync(); }
        finally { foreach (var root in _roots) if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
    private sealed record World(AgentRunLogCompleteRequest Complete, Guid[] ObjectIds, string WholeSha256, int SegmentSize, int Segments);
}
