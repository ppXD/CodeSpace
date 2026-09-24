using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using CodeSpace.NativeLaunch;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The owning host's backstop for a CANCELLED run whose agent is still alive, driven through the REAL reconciler against
/// real Postgres and a REAL durable launch. The owner normally stops its own agent the moment it sees the lost fence;
/// this is what stops it when that owner is gone, could not confirm the kill, or lost the run to a reclaim first —
/// every other sweep selects Running rows only, so without it a Cancelled row over a live process is nobody's job.
///
/// <para>Fidelity: HIGH. The agent is a real <c>/bin/sh</c> under the native launch protocol and every verdict is read
/// off the OS by the product's identity-bound oracle (<c>NativeProcess.IsAlive</c>). Assertions are on the process this
/// test launched, never on the sweep's deployment-wide tally, which other suites' rows share. The sweep takes the OLDEST
/// candidates first, so every live row here is stamped well inside its window but older than anything another suite's
/// run writes, and every row this class seeds gives its handle back in <see cref="Dispose"/> — the state the spool
/// reaper leaves — so no batch of it can crowd a later suite out. POSIX-only (Rule 12.1); every launch is terminated
/// and its directories removed in <see cref="Dispose"/> (Rule 12.3).</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunCancelledAgentSweepFlowTests : IDisposable
{
    private readonly PostgresFixture _fixture;
    private readonly List<SandboxHandle> _launched = new();
    private readonly List<string> _directories = new();
    private readonly List<Guid> _seededRuns = new();

    public AgentRunCancelledAgentSweepFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>What the run's row says about the live process the test launched.</summary>
    public enum RecordedHandle
    {
        /// <summary>The native handle exactly as this host's launch minted it.</summary>
        LaunchedHere,

        /// <summary>The same launch, stamped as another host's — a pid in a namespace this sweep cannot address.</summary>
        LaunchedElsewhere,

        /// <summary>A pre-host, pre-native handle naming the same pid and nothing else. Nothing but the number identifies it, and the number may name any process by now.</summary>
        PidOnly,
    }

    [Theory]
    [InlineData(AgentRunStatus.Cancelled, RecordedHandle.LaunchedHere, true)]        // cancelled elsewhere, agent alive HERE → the host that launched it stops it
    [InlineData(AgentRunStatus.Cancelled, RecordedHandle.LaunchedElsewhere, false)]  // another host's launch → never judged here
    [InlineData(AgentRunStatus.Cancelled, RecordedHandle.PidOnly, false)]            // never a kill by pid alone, however alive that pid is
    [InlineData(AgentRunStatus.Running, RecordedHandle.LaunchedHere, false)]         // a live, leased run belongs to its owner — only a cancel orphans the agent
    public async Task The_sweep_stops_a_cancelled_runs_agent_only_on_the_host_that_launched_it(AgentRunStatus status, RecordedHandle recordedAs, bool expectStopped)
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchSleeperAsync();
        var agent = handle.NativeLaunch.ShouldNotBeNull("a durable launch carries its native execution identity").Execution.ShouldNotBeNull();

        await SeedRunAsync(await SeedTeamAsync(), status, Recorded(handle, recordedAs));

        NativeProcess.IsAlive(agent).ShouldBeTrue($"precondition: the agent is running before the sweep — {ProcessLiveness.Describe(handle)}");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        if (expectStopped)
            NativeProcess.IsAlive(agent).ShouldBeFalse($"a Cancelled run's agent was still running on the host that launched it, and the sweep on that host left it — it would run on to its wall-clock deadline; diagnose with {ProcessLiveness.Describe(handle)}");
        else
            NativeProcess.IsAlive(agent).ShouldBeTrue($"a {status} run recorded as {recordedAs} is not this sweep's to kill — {ProcessLiveness.Describe(handle)}");
    }

    /// <summary>
    /// The sweep's horizon. A cancel older than <see cref="AgentRunReconcilerService.CancelledAgentWindow"/> is past the
    /// point where the spool reaper reclaims the launch files the kill identifies its process by, and outside the index
    /// range the query reads — so even a live agent there is not this sweep's. Drop the window and the oldest-first batch
    /// picks this row up first.
    /// </summary>
    [Fact]
    public async Task The_sweep_leaves_a_cancelled_agent_older_than_its_window()
    {
        if (OperatingSystem.IsWindows()) return;

        var handle = await LaunchSleeperAsync();
        var agent = handle.NativeLaunch.ShouldNotBeNull().Execution.ShouldNotBeNull();

        await SeedRunAsync(await SeedTeamAsync(), AgentRunStatus.Cancelled, handle, cancelledAgo: AgentRunReconcilerService.CancelledAgentWindow + TimeSpan.FromHours(1));

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        NativeProcess.IsAlive(agent).ShouldBeTrue($"a cancel older than the sweep's window must not be read at all — {ProcessLiveness.Describe(handle)}");
    }

    /// <summary>What fills the batch ahead of — or behind — the one live cancelled agent.</summary>
    public enum Decoys
    {
        /// <summary>A full batch of NEWER cancels whose agent is already dead: the shape of every cancel an owner handled itself. Oldest-first keeps them behind the live one.</summary>
        DeadAndNewer,

        /// <summary>A full batch of OLDER cancels whose handle's deadline has passed: the RunnerHost stopped those agents at that instant, so they cannot be alive and must not take a slot.</summary>
        PastTheirDeadlineAndOlder,

        /// <summary>A full batch of OLDER cancels that recorded no handle at all: nothing to identify, nothing to probe.</summary>
        WithoutAHandleAndOlder,
    }

    /// <summary>
    /// The starvation the batch bound could cause: <see cref="AgentRunReconcilerService.BatchSize"/> rows that cannot be
    /// a live orphan must never keep the one that is out of the batch. Pinned both ways the query rules them out —
    /// order (the newer dead rows stay behind) and bounds (the older impossible rows never qualify).
    /// </summary>
    [Theory]
    [InlineData(Decoys.DeadAndNewer)]
    [InlineData(Decoys.PastTheirDeadlineAndOlder)]
    [InlineData(Decoys.WithoutAHandleAndOlder)]
    public async Task A_full_batch_of_rows_that_cannot_be_alive_never_hides_a_live_cancelled_agent(Decoys decoys)
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        var dead = await LaunchSleeperAsync();

        using (var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            (await new LocalProcessRunner().TerminateAsync(dead, cleanup.Token)).IsSettled.ShouldBeTrue("precondition: the decoys' agent is dead");

        await SeedDecoysAsync(teamId, decoys, dead);

        var live = await LaunchSleeperAsync();
        var agent = live.NativeLaunch.ShouldNotBeNull().Execution.ShouldNotBeNull();

        await SeedRunAsync(teamId, AgentRunStatus.Cancelled, live);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IAgentRunReconcilerService>().ReconcileAsync(CancellationToken.None);

        NativeProcess.IsAlive(agent).ShouldBeFalse($"{AgentRunReconcilerService.BatchSize} {decoys} rows kept the one live cancelled agent out of the batch; diagnose with {ProcessLiveness.Describe(live)}");
    }

    private Task SeedDecoysAsync(Guid teamId, Decoys decoys, SandboxHandle dead) => decoys switch
    {
        Decoys.DeadAndNewer => SeedRunsAsync(teamId, AgentRunStatus.Cancelled, dead, TimeSpan.FromMinutes(1), AgentRunReconcilerService.BatchSize),
        Decoys.PastTheirDeadlineAndOlder => SeedRunsAsync(teamId, AgentRunStatus.Cancelled, dead with { Deadline = DateTimeOffset.UtcNow.AddHours(-1) }, InsideTheWindow + TimeSpan.FromHours(1), AgentRunReconcilerService.BatchSize),
        Decoys.WithoutAHandleAndOlder => SeedRunsAsync(teamId, AgentRunStatus.Cancelled, null, InsideTheWindow + TimeSpan.FromHours(1), AgentRunReconcilerService.BatchSize),
        _ => throw new ArgumentOutOfRangeException(nameof(decoys)),
    };

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>A REAL long-lived agent under the local durable runner, on THIS host — the only process the sweep may ever reach.</summary>
    private async Task<SandboxHandle> LaunchSleeperAsync()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "cs-cancel-sweep-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        _directories.Add(workDir);

        var spec = new SandboxSpec { Command = "/bin/sh", Args = new[] { "-c", "sleep 300" }, WorkingDirectory = workDir, TimeoutSeconds = 300 };

        SandboxHandle handle;
        using (var scope = _fixture.BeginScope())
            handle = await ((ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind)).LaunchAsync(spec, Guid.NewGuid().ToString("N"), CancellationToken.None);

        _launched.Add(handle);
        _directories.Add(handle.SpoolDirectory);

        return handle;
    }

    /// <summary>The row's view of <paramref name="launched"/>, in the requested shape.</summary>
    private SandboxHandle Recorded(SandboxHandle launched, RecordedHandle shape) => shape switch
    {
        RecordedHandle.LaunchedHere => launched,
        RecordedHandle.LaunchedElsewhere => launched with { LaunchHost = $"another-worker-{Guid.NewGuid():N}" },
        RecordedHandle.PidOnly => new SandboxHandle { Kind = LocalProcessRunner.LocalKind, ProcessId = launched.ProcessId, SpoolDirectory = EmptyDirectory(), Deadline = launched.Deadline },
        _ => throw new ArgumentOutOfRangeException(nameof(shape)),
    };

    /// <summary>A spool directory with nothing in it: no launch files to bind, no exit marker to read — the pid is all a handle pointing here has.</summary>
    private string EmptyDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cs-cancel-sweep-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _directories.Add(directory);

        return directory;
    }

    /// <summary>When a live candidate here was cancelled: well inside the sweep's window, and older than anything another suite's run can have written — so it heads an oldest-first batch whatever else shares the database.</summary>
    private static TimeSpan InsideTheWindow => AgentRunReconcilerService.CancelledAgentWindow - TimeSpan.FromHours(2);

    /// <summary>A run as its last writer left it: Cancelled by the API pod <paramref name="cancelledAgo"/> (default <see cref="InsideTheWindow"/>), or Running under a fresh lease its live owner keeps renewing.</summary>
    private Task SeedRunAsync(Guid teamId, AgentRunStatus status, SandboxHandle? handle, TimeSpan? cancelledAgo = null) => SeedRunsAsync(teamId, status, handle, cancelledAgo ?? InsideTheWindow, count: 1);

    private async Task SeedRunsAsync(Guid teamId, AgentRunStatus status, SandboxHandle? handle, TimeSpan cancelledAgo, int count)
    {
        var now = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        for (var i = 0; i < count; i++)
        {
            var runId = Guid.NewGuid();
            _seededRuns.Add(runId);

            db.AgentRun.Add(new AgentRun
            {
                Id = runId, TeamId = teamId, Harness = "codex-cli", Status = status, FenceEpoch = 2, OwnerId = Guid.NewGuid(),
                StartedAt = (status == AgentRunStatus.Running ? now : now - cancelledAgo) - TimeSpan.FromMinutes(1), HeartbeatAt = now, LeaseExpiresAt = now + AgentRunLiveness.LeaseDuration,
                CompletedAt = status == AgentRunStatus.Running ? null : now - cancelledAgo, Error = status == AgentRunStatus.Cancelled ? "operator cancel" : null,
                RunnerHandleJson = handle is null ? null : JsonSerializer.Serialize(handle, AgentJson.Options),
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"cancel-sweep-{userId:N}@test.local", Name = $"cancel-sweep-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"cancel-sweep-{teamId:N}", Name = "Cancel Sweep Team", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return teamId;
    }

    public void Dispose()
    {
        foreach (var handle in _launched)
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                new LocalProcessRunner().TerminateAsync(handle, cleanup.Token).GetAwaiter().GetResult();
            }
            catch { /* best-effort: a process the sweep already reaped has nothing left to stop */ }
        }

        foreach (var directory in _directories)
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }

        try
        {
            using var scope = _fixture.BeginScope();
            var seeded = _seededRuns.ToArray();
            scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolated($"UPDATE agent_run SET runner_handle = NULL WHERE id = ANY({seeded})");
        }
        catch { /* best-effort: a row that keeps its handle is still dead, and leaves the window on its own */ }
    }
}
