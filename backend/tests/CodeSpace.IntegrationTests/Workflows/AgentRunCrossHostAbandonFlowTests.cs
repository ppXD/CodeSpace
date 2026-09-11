using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Recovery;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Settings;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Recovery;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The cross-host abandon: a run whose host died is terminalized by a sweep on a DIFFERENT worker, which can reach
/// none of that run's host-local resources. Before this slice the abandon ran the local netns and cgroup teardowns
/// against foreign keys anyway, swallowed the miss as a best-effort warning, and left no record of a single thing
/// still standing — including the egress-subnet lease, which is released on the host that HOLDS it and so was never
/// returned to a bounded pool.
///
/// <para>Both halves are proven here against real Postgres: the foreign reconciler that may only make a CLAIM, and
/// the sweep on the owning host that is the only thing able to settle it. The two "hosts" are one process wearing two
/// GUID-unique <see cref="LocalProcessRunner.SandboxHostEnvVar"/> identities, which is exactly the boundary
/// production compares — so every host-scoped query in both sweeps sees only this test's own row.</para>
///
/// <para>Tier: high-fidelity Integration — the real <see cref="IAgentRunReconcilerService"/>,
/// <see cref="IAgentRunOrphanReaper"/>, <see cref="IAgentRunSpoolReaper"/> and <see cref="IRunCleanupLedger"/> over
/// real Postgres and a real spool tree. The kernel teardowns are absent on a developer Mac, which the assertions pin
/// via <c>IsSupported</c> rather than skip.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunCrossHostAbandonFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly string _spoolRoot = Path.Combine(Path.GetTempPath(), "codespace-crosshost-" + Guid.NewGuid().ToString("N"));
    private readonly string _cloneRoot = Path.Combine(Path.GetTempPath(), "codespace-crosshost-clones-" + Guid.NewGuid().ToString("N"));
    private readonly IDisposable _settings;
    private readonly string? _previousHost;

    public AgentRunCrossHostAbandonFlowTests(PostgresFixture fixture)
    {
        _fixture = fixture;
        _settings = RuntimeSettings.Override(value => value with { AgentRunSpoolDirectory = _spoolRoot });
        _previousHost = Environment.GetEnvironmentVariable(LocalProcessRunner.SandboxHostEnvVar);
    }

    [Fact]
    public async Task A_run_abandoned_from_another_host_records_one_orphan_per_resource_and_reclaims_nothing()
    {
        var run = await AbandonFromTheOtherHostAsync();

        using var scope = _fixture.BeginScope();
        var actual = await scope.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(row => row.Id == run.Id);
        actual.Status.ShouldBe(AgentRunStatus.Failed, "a foreign handle past its deadline must still reach a terminal state");

        var receipts = await ReceiptsAsync(run);

        receipts.Where(receipt => receipt.Outcome == RunResourceOutcome.Orphaned).Select(receipt => receipt.Kind).ShouldBe(
            [RunResourceKind.Spool, RunResourceKind.McpSocket, RunResourceKind.EgressSubnet, RunResourceKind.Cgroup, RunResourceKind.Workspace],
            ignoreOrder: true,
            customMessage: "each of these is host-local, so the abandoning worker can do nothing with it but name it");

        receipts.ShouldAllBe(receipt => !receipt.IsSettled,
            customMessage: "a Completed/Compensated row here would re-assert the pre-fix lie that the local teardowns freed a foreign netns and cgroup");
        receipts.Where(receipt => receipt.Outcome == RunResourceOutcome.Orphaned).ShouldAllBe(receipt => receipt.OwnerHost == run.OwnerHost);
        receipts.ShouldAllBe(receipt => receipt.RecordedByHost == run.SweepingHost);
        receipts.ShouldAllBe(receipt => receipt.FenceEpoch == 2, "the abandon's own CAS bumped the fence to 2, and that is the generation these resources belong to");

        receipts.Single(receipt => receipt.Kind == RunResourceKind.ProviderCredentialLease).Outcome.ShouldBe(RunResourceOutcome.Unknown,
            "the agent may have been mid-call on the dead host, and no sweep can ever establish otherwise");

        Directory.Exists(run.SpoolDirectory).ShouldBeTrue("the foreign abandon deletes nothing — the spool belongs to the host that owns it");
        Directory.Exists(run.WorkspaceDirectory).ShouldBeTrue();
    }

    [Fact]
    public async Task The_owning_host_settles_only_what_it_can_actually_reclaim()
    {
        var run = await AbandonFromTheOtherHostAsync();

        // The lost host comes back (or its replacement adopts its identity) and sweeps its own outstanding orphans.
        Environment.SetEnvironmentVariable(LocalProcessRunner.SandboxHostEnvVar, run.OwnerHost);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunOrphanReaper>().ReapAsync(CancellationToken.None);

        var afterKernelSweep = await ReceiptsAsync(run);

        // On a host with ip/nft (the privileged Linux CI job) the netns is really torn down and the subnet lease
        // really returned; on a developer Mac nothing can be attempted, which must read Unknown and never Compensated.
        afterKernelSweep.Single(receipt => receipt.Kind == RunResourceKind.EgressSubnet).Outcome.ShouldBe(
            FilteredEgressNetns.IsSupported ? RunResourceOutcome.Compensated : RunResourceOutcome.Unknown,
            customMessage: "a reclaim that could not be attempted is not a reclaim");
        afterKernelSweep.Single(receipt => receipt.Kind == RunResourceKind.Cgroup).Outcome.ShouldBe(
            CgroupResourceLimit.IsSupported && CgroupResourceLimit.CgroupRoot is not null ? RunResourceOutcome.Compensated : RunResourceOutcome.Unknown);

        afterKernelSweep.Where(receipt => receipt.Kind is RunResourceKind.Spool or RunResourceKind.McpSocket or RunResourceKind.Workspace)
            .ShouldAllBe(receipt => receipt.Outcome == RunResourceOutcome.Orphaned,
                customMessage: "these are still on disk under the spool reaper's and workspace janitor's own retention policies, and an orphan receipt is not authority to pre-empt either");

        // The owning host's ordinary spool reaper reaches the run once its retention window has passed. Host-scoped by
        // launch host, and this run's host identity is GUID-unique, so its candidate set is exactly this row.
        await AgeThePoolPastRetentionAsync(run);
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunSpoolReaper>().ReapAsync(CancellationToken.None)).ShouldBe(1);

        Directory.Exists(run.SpoolDirectory).ShouldBeFalse("precondition: the production spool reaper removed the directory family");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IAgentRunOrphanReaper>().ReapAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(2);

        var settled = await ReceiptsAsync(run);
        settled.Single(receipt => receipt.Kind == RunResourceKind.Spool).Outcome.ShouldBe(RunResourceOutcome.Compensated,
            "the orphan was real and it was repaired — recording that is the difference between a fixed orphan and a hoped-for one");
        settled.Single(receipt => receipt.Kind == RunResourceKind.McpSocket).Outcome.ShouldBe(RunResourceOutcome.Compensated, "the socket lived inside the spool");
        settled.Single(receipt => receipt.Kind == RunResourceKind.Workspace).Outcome.ShouldBe(RunResourceOutcome.Orphaned,
            "the clone is outside the spool tree and only the workspace janitor's age policy reclaims it — claiming otherwise would be a fiction");
        settled.Single(receipt => receipt.Kind == RunResourceKind.Spool).RecordedByHost.ShouldBe(run.OwnerHost);
    }

    [Fact]
    public async Task A_settled_resource_is_never_regressed_to_an_orphan_claim()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedBareRunAsync(teamId);
        var stamp = new RunCleanupStamp(runId, 1, "host-z", DateTimeOffset.UtcNow);

        using (var scope = _fixture.BeginScope())
        {
            var ledger = scope.Resolve<IRunCleanupLedger>();
            await ledger.UpsertAsync(stamp.Completed(RunResourceKind.EgressSubnet, "host-z", "netns-key"), CancellationToken.None);
            await ledger.UpsertAsync(stamp.Orphaned(RunResourceKind.EgressSubnet, "host-z", "netns-key"), CancellationToken.None);
        }

        using var verify = _fixture.BeginScope();
        var receipt = (await verify.Resolve<IRunCleanupLedger>().ForRunsAsync(teamId, [runId], CancellationToken.None)).ShouldHaveSingleItem();
        receipt.Outcome.ShouldBe(RunResourceOutcome.Completed,
            "the resource is provably gone, so a later sweep's 'still orphaned' read of it is stale by construction, not news");
    }

    [Fact]
    public async Task An_orphan_that_names_no_host_is_refused_by_the_ledger_and_by_the_database()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedBareRunAsync(teamId);
        var ownerless = new RunCleanupReceipt
        {
            AgentRunId = runId, FenceEpoch = 1, Kind = RunResourceKind.Spool, Outcome = RunResourceOutcome.Orphaned,
            RecordedByHost = "host-z", RecordedAt = DateTimeOffset.UtcNow,
        };

        using var scope = _fixture.BeginScope();
        await Should.ThrowAsync<ArgumentException>(() => scope.Resolve<IRunCleanupLedger>().UpsertAsync(ownerless, CancellationToken.None));

        // Defence in depth: the constraint holds even for a writer that never passes through the ledger.
        var direct = await Should.ThrowAsync<PostgresException>(() => scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO agent_run_cleanup_receipt (id, team_id, agent_run_id, fence_epoch, kind, outcome, recorded_by_host, recorded_at)
            VALUES ({Guid.NewGuid()}, {teamId}, {runId}, 1, 'Spool', 'Orphaned', 'host-z', clock_timestamp())
            """, CancellationToken.None));
        direct.ConstraintName.ShouldBe("ck_agent_run_cleanup_receipt_orphan");
    }

    /// <summary>Stage a run launched on one host, then reconcile it from another — the exact state a killed pod leaves behind. Returns the identities the assertions need.</summary>
    private async Task<AbandonedRun> AbandonFromTheOtherHostAsync()
    {
        var ownerHost = "host-a-" + Guid.NewGuid().ToString("N");
        var sweepingHost = "host-b-" + Guid.NewGuid().ToString("N");
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runId = Guid.NewGuid();
        var spool = Path.Combine(_spoolRoot, runId.ToString("N"));
        var clone = Path.Combine(_cloneRoot, runId.ToString("N"));
        Directory.CreateDirectory(spool);
        Directory.CreateDirectory(clone);
        await File.WriteAllTextAsync(Path.Combine(spool, "out.log"), "raw output nobody here can reach");

        // A plain file stands in for the tool-fabric socket's inode. The only property under test is that the entry
        // still EXISTS on the owning host's disk, which is exactly what the sweep reads — binding a real
        // UnixDomainSocketEndPoint here would add nothing but the platform's ~104-byte path limit.
        await File.WriteAllTextAsync(Path.Combine(spool, "mcp.sock"), "");

        var handle = new SandboxHandle
        {
            Kind = "local", ProcessId = DeadPid(), LaunchHost = ownerHost, SpoolDirectory = spool,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(-1),   // past its own wall clock: no observer can still be completing it
            McpSocketPath = Path.Combine(spool, "mcp.sock"), EgressNetnsKey = runId.ToString("N"), CgroupRunKey = runId.ToString("N"),
            WorkspaceDirectory = clone, WorkspaceBaseSha = new string('a', 40), InjectedKeyFingerprint = "sha256:deadbeef",
        };

        var stamp = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(20);
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.AgentRun.Add(new AgentRun
            {
                Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Running, FenceEpoch = 1,
                StartedAt = stamp, HeartbeatAt = stamp, LeaseExpiresAt = stamp + AgentRunLiveness.Window,
                RunnerHandleJson = JsonSerializer.Serialize(handle, AgentJson.Options),
            });
            await db.SaveChangesAsync();
        }

        Environment.SetEnvironmentVariable(LocalProcessRunner.SandboxHostEnvVar, sweepingHost);
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        return new AbandonedRun(teamId, runId, ownerHost, sweepingHost, spool, clone);
    }

    private async Task<IReadOnlyList<RunCleanupReceipt>> ReceiptsAsync(AbandonedRun run)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IRunCleanupLedger>().ForRunsAsync(run.TeamId, [run.Id], CancellationToken.None);
    }

    private async Task AgeThePoolPastRetentionAsync(AbandonedRun run)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().AgentRun.Where(row => row.Id == run.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.CompletedAt, DateTimeOffset.UtcNow - AgentRunSpoolReaper.Retention - TimeSpan.FromHours(1)));
    }

    private async Task<Guid> SeedBareRunAsync(Guid teamId)
    {
        var runId = Guid.NewGuid();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.AgentRun.Add(new AgentRun { Id = runId, TeamId = teamId, Harness = "codex-cli", Status = AgentRunStatus.Failed, FenceEpoch = 1, CompletedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return runId;
    }

    /// <summary>A pid that resolves to nothing, so no assertion can accidentally depend on a live process.</summary>
    private static int DeadPid() => 2_000_000_000;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(LocalProcessRunner.SandboxHostEnvVar, _previousHost);
        _settings.Dispose();
        foreach (var root in new[] { _spoolRoot, _cloneRoot })
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { /* best-effort */ }
    }

    private sealed record AbandonedRun(Guid TeamId, Guid Id, string OwnerHost, string SweepingHost, string SpoolDirectory, string WorkspaceDirectory);
}
