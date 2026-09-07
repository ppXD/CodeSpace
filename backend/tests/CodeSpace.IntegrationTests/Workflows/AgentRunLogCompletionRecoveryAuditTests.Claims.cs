using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.StorageTestWorker;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

public sealed partial class AgentRunLogCompletionRecoveryAuditTests
{
    [Theory]
    [InlineData("owner")]
    [InlineData("fence")]
    [InlineData("intent")]
    [InlineData("expired")]
    public async Task Recovery_verification_rejects_noncurrent_claims_before_physical_reads(string mismatch)
    {
        var world = await SeedAsync(declareRecovery: true, segmentCount: 1, segmentBytes: 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var claim = await ClaimAsync(world, Guid.NewGuid(), mismatch == "expired" ? TimeSpan.FromMilliseconds(50) : TimeSpan.FromSeconds(30));
        if (mismatch == "expired") await WaitForClaimExpiryAsync(claim.IntentId, deadline.Token);
        claim = mismatch switch
        {
            "owner" => claim with { OwnerId = Guid.NewGuid() },
            "fence" => claim with { FenceEpoch = claim.FenceEpoch + 1 },
            "intent" => claim with { IntentId = Guid.NewGuid() },
            _ => claim,
        };
        using var scope = fixture.BeginScope();
        var probe = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>());
        var result = (await Logs(scope, probe).CompleteAsync(world.Complete with { RecoveryClaim = claim }, deadline.Token)).ShouldBeOfType<AgentRunLogCompleteResult.Rejected>();
        result.Problem.Code.ShouldBe(AgentRunLogProblemCode.StaleRecoveryClaim);
        probe.Reads.ShouldBeEmpty();
        (await scope.Resolve<CodeSpaceDbContext>().AgentRunLogVerification.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_lease_reclaimed_after_real_eof_cannot_checkpoint_or_seal_for_the_old_owner()
    {
        var world = await SeedAsync(declareRecovery: true, segmentCount: 1, segmentBytes: 4096);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        var old = await ClaimAsync(world, Guid.NewGuid(), TimeSpan.FromSeconds(30));
        using var scope = fixture.BeginScope();
        AgentRunLogRecoveryClaimRef? current = null;
        var read = new ManifestReadFault(scope.Resolve<IArtifactCasRuntimeCoordinator>(), "none", async token =>
        {
            await WaitForClaimExpiryAsync(old.IntentId, token);
            current = await ClaimAsync(world, Guid.NewGuid(), TimeSpan.FromSeconds(30));
        });
        var rejected = (await Logs(scope, read).CompleteAsync(world.Complete with { RecoveryClaim = old }, deadline.Token)).ShouldBeOfType<AgentRunLogCompleteResult.Rejected>();
        read.ReadStarted.ShouldBeTrue();
        read.Disposed.ShouldBeTrue();
        current.ShouldNotBeNull();
        rejected.Problem.Code.ShouldBe(AgentRunLogProblemCode.StaleRecoveryClaim);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var checkpoint = await db.AgentRunLogVerification.AsNoTracking().SingleAsync();
        checkpoint.NextSegmentOrdinal.ShouldBe(1);
        checkpoint.VerifiedBytes.ShouldBe(0);
        checkpoint.SealedAt.ShouldBeNull();
        var staleMutation = await Should.ThrowAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_verification SET revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {checkpoint.Id}"));
        staleMutation.SqlState.ShouldBe("P0113", "the database independently fences the old receipt writer, even after a physical read reached EOF");
        var completed = (await Logs(scope, scope.Resolve<IArtifactCasRuntimeCoordinator>()).CompleteAsync(world.Complete with { RecoveryClaim = current }, deadline.Token)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        AssertManifest(world, completed.Metadata);
    }

    [Theory]
    [InlineData("close")]
    [InlineData("stall")]
    public async Task Repeated_reads_without_a_durable_segment_checkpoint_exhaust_the_bounded_retry_policy(string fault)
    {
        var world = await SeedAsync(declareRecovery: true, segmentCount: 1, segmentBytes: 4096);
        await MarkTerminalAsync(world);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var scope = fixture.BeginScope();
            var read = new ManifestReadFault(scope.Resolve<IArtifactCasRuntimeCoordinator>(), fault);
            var recovery = Recovery(scope, Logs(scope, read));
            AgentRunLogCaptureRecoverySummary summary;
            do
            {
                summary = await recovery.ReconcileAsync(deadline.Token);
                if (summary.Claimed == 0) await Task.Delay(10, deadline.Token);
            } while (summary.Claimed == 0);
            summary.LostLease.ShouldBe(0);
            read.ReadStarted.ShouldBeTrue();
            read.Disposed.ShouldBeTrue();
            var intent = await IntentAsync(world);
            intent.RecoveryAttemptCount.ShouldBe(attempt);
            intent.VerificationProgressOrdinal.ShouldBe(0);
            intent.VerificationStalledAttempts.ShouldBe(attempt);
            intent.LastVerificationProgressAt.ShouldBeNull();
            var checkpoint = await scope.Resolve<CodeSpaceDbContext>().AgentRunLogVerification.AsNoTracking().SingleAsync();
            checkpoint.NextSegmentOrdinal.ShouldBe(1);
            checkpoint.VerifiedBytes.ShouldBe(0);
            checkpoint.SealedAt.ShouldBeNull();
            if (attempt < 3) intent.State.ShouldNotBe(AgentRunLogCaptureIntentState.ExternalStateIndeterminate);
            else
            {
                summary.ExternalStateIndeterminate.ShouldBe(1);
                intent.State.ShouldBe(AgentRunLogCaptureIntentState.ExternalStateIndeterminate);
                intent.LastErrorCode.ShouldBe("recovery-exhausted");
                intent.LastErrorMessage.ShouldContain(fault == "stall" ? "recovery-operation-timeout" : "complete-backend-unavailable");
                (await recovery.ReconcileAsync(deadline.Token)).Claimed.ShouldBe(0);
            }
        }
    }

    [Fact]
    public async Task Lost_final_seal_commit_ack_returns_the_same_manifest_without_reading_physical_objects_again()
    {
        var world = await SeedAsync(declareRecovery: false);
        using var scope = fixture.BeginScope();
        var dropped = new LoseCheckpointAck(fixture.ConnectionString, world.Complete.StreamId, seal: true);
        var options = new DbContextOptionsBuilder<CodeSpaceDbContext>(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>()).AddInterceptors(dropped).Options;
        var reads = new LogCompletionReadProbe(scope.Resolve<IArtifactCasRuntimeCoordinator>());
        await Should.ThrowAsync<IOException>(async () => await new AgentRunLogService(options, reads, TimeProvider.System).CompleteAsync(world.Complete, CancellationToken.None));
        dropped.Dropped.ShouldBeTrue();
        AssertReads(reads.Reads.ToArray(), world.ObjectIds);
        using var retryScope = fixture.BeginScope();
        var retry = new LogCompletionReadProbe(retryScope.Resolve<IArtifactCasRuntimeCoordinator>());
        var completed = (await Logs(retryScope, retry).CompleteAsync(world.Complete, CancellationToken.None)).ShouldBeOfType<AgentRunLogCompleteResult.Completed>();
        AssertManifest(world, completed.Metadata);
        retry.Reads.ShouldBeEmpty();
    }

    private async Task<AgentRunLogRecoveryClaimRef> ClaimAsync(World world, Guid owner, TimeSpan lease)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var intent = await IntentAsync(world);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_log_capture_intent SET recovery_owner_id = {owner}, recovery_fence_epoch = recovery_fence_epoch + 1, recovery_attempt_count = recovery_attempt_count + 1, verification_claim_marker = verification_claim_marker + 1, recovery_started_at = COALESCE(recovery_started_at, clock_timestamp()), recovery_lease_expires_at = clock_timestamp() + {lease}, revision = revision + 1, last_modified_at = clock_timestamp() WHERE id = {intent.Id}");
        return new AgentRunLogRecoveryClaimRef(intent.Id, owner, intent.RecoveryFenceEpoch + 1);
    }

    private async Task WaitForClaimExpiryAsync(Guid intentId, CancellationToken cancellationToken)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        while (!await db.Database.SqlQuery<bool>($"SELECT recovery_lease_expires_at <= clock_timestamp() AS \"Value\" FROM agent_run_log_capture_intent WHERE id = {intentId}").SingleAsync(cancellationToken))
            await Task.Delay(10, cancellationToken);
    }
}
