using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Exceptions;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

public partial class AgentRunExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_launch_handle_write_failure_preserves_live_execution_for_recovery(bool loseCommittedAcknowledgement)
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "launch-ack-window");
        var repositoryId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, "main");
        Guid runId;
        using (var seed = await CodeSpace.IntegrationTests.Workflows.Infrastructure.WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId))
            runId = (await seed.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = "edit", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId, TimeoutSeconds = 20 }, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
        var harness = new ScriptedHarness("printf 'started\n' > launch-effect; while :; do sleep 1; done");
        var fault = new RunnerHandleAcknowledgementFault(loseCommittedAcknowledgement);
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            using (var execute = _fixture.BeginScope(builder =>
            {
                var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(fault).Options;
                builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
                builder.RegisterInstance(new AgentHarnessRegistry([harness])).As<IAgentHarnessRegistry>();
            }))
                await execute.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, bounded.Token);

            fault.Fired.ShouldBeTrue("the injected fault must occur at the first real runner_handle SQL write, after the shell produced its effect");
            var handle = fault.Handle.ShouldNotBeNull();
            var launchDirectory = Path.Combine(handle.SpoolDirectory, NativeLaunchProtocol.DirectoryName);
            var request = JsonSerializer.Deserialize<NativeLaunchRecord>(await File.ReadAllTextAsync(Path.Combine(launchDirectory, NativeLaunchProtocol.RequestFile), bounded.Token), NativeLaunchProtocol.Json).ShouldNotBeNull();
            var receipt = JsonSerializer.Deserialize<NativeLaunchReceipt>(await File.ReadAllTextAsync(Path.Combine(launchDirectory, NativeLaunchProtocol.ReceiptFile), bounded.Token), NativeLaunchProtocol.Json).ShouldNotBeNull();
            receipt.Execution.ShouldNotBeNull().ProcessId.ShouldBe(handle.ProcessId);
            receipt.SpecHash.ShouldBe(request.SpecHash);
            var probe = await new LocalProcessRunner().ProbeAsync(handle, bounded.Token);
            probe.State.ShouldBe(SandboxRunState.Running, "the failure window must contain a real live process, not a simulated handle");

            using var verify = _fixture.BeginScope();
            var db = verify.Resolve<CodeSpaceDbContext>();
            var run = await db.AgentRun.AsNoTracking().SingleAsync(row => row.Id == runId, bounded.Token);
            (run.RunnerHandleJson is not null).ShouldBe(loseCommittedAcknowledgement, "the before-write and committed-but-unacknowledged cases must have distinct durable DB outcomes");
            var attempts = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().Where(row => row.AgentRunId == runId).Select(row => new { row.Id, row.ExecutionId, row.State, row.StartedAt, row.RunnerLocatorJson }).ToArrayAsync(bounded.Token);
            var evidence = JsonSerializer.Serialize(new { loseCommittedAcknowledgement, run.Status, run.Error, HandleCommitted = run.RunnerHandleJson is not null, WorkspacePresent = Directory.Exists(handle.WorkspaceDirectory), ProcessState = probe.State, receipt.Execution, RequestIdentity = request.Identity, Attempts = attempts }, AgentJson.Options);
            run.Status.ShouldBe(AgentRunStatus.Running, "an accepted native process with a failed app handle acknowledgement must remain recoverable; observed: " + evidence);
            Directory.Exists(handle.WorkspaceDirectory).ShouldBeTrue("a live execution owns this real git clone until explicitly terminated or safely recovered");
        }
        finally
        {
            if (fault.Handle is { } handle)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await new LocalProcessRunner().TerminateAsync(handle, cleanup.Token);
                var stopped = await new LocalProcessRunner().ProbeAsync(handle, cleanup.Token);
                stopped.State.ShouldNotBe(SandboxRunState.Running, "the test must not leak its live child");
                if (handle.WorkspaceDirectory is { } workspace && Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private sealed class RunnerHandleAcknowledgementFault(bool afterCommit) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public SandboxHandle? Handle { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired || !command.CommandText.Contains("SET runner_handle =", StringComparison.Ordinal)) return result;
            var json = command.Parameters.Cast<DbParameter>().Select(parameter => parameter.Value).OfType<string>().Single(value => value.Contains("spoolDirectory", StringComparison.Ordinal));
            Handle = JsonSerializer.Deserialize<SandboxHandle>(json, AgentJson.Options).ShouldNotBeNull();
            var watch = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(Handle.WorkspaceDirectory!, "launch-effect")) && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20, cancellationToken);
            File.Exists(Path.Combine(Handle.WorkspaceDirectory!, "launch-effect")).ShouldBeTrue("the actual workload must have started before fault injection");
            if (!afterCommit)
            {
                Fired = true;
                throw new IOException("Injected runner handle failure before SQL execution");
            }
            return result;
        }

        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Fired || !afterCommit || !command.CommandText.Contains("SET runner_handle =", StringComparison.Ordinal)) return ValueTask.FromResult(result);
            result.ShouldBe(1, "the real autocommit UPDATE must succeed before its acknowledgement is lost");
            Fired = true;
            throw new IOException("Injected runner handle autocommit succeeded but acknowledgement was lost");
        }
    }
}

public partial class AgentRunExecutorTests
{
    /// <summary>
    /// The other half of the acknowledgement window: a run left Running with a LIVE process and no durable address is
    /// only recoverable if something can still find that process. This drives the real reconciler over a real crashed
    /// launch and asserts it re-addresses the run by the attempt's exact identity — and that the same slot refuses to
    /// be adopted for any other attempt, which is what stops adoption from becoming a way to seize a foreign process.
    /// </summary>
    [Fact]
    public async Task Reconciler_adopts_an_admitted_execution_by_its_exact_attempt_identity()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "launch-adoption");
        var repositoryId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, "main");
        Guid runId;
        using (var seed = await CodeSpace.IntegrationTests.Workflows.Infrastructure.WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId))
            runId = (await seed.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = "edit", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId, TimeoutSeconds = 120 }, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
        var harness = new ScriptedHarness("printf 'started\n' > launch-effect; while :; do sleep 1; done");
        var fault = new RunnerHandleAcknowledgementFault(afterCommit: false);
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            using (var execute = _fixture.BeginScope(builder =>
            {
                var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(fault).Options;
                builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
                builder.RegisterInstance(new AgentHarnessRegistry([harness])).As<IAgentHarnessRegistry>();
            }))
                await execute.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, bounded.Token);

            var handle = fault.Handle.ShouldNotBeNull();

            // The state the previous test pins, restated as this one's precondition: a live process, no durable address.
            Guid attemptId;
            using (var before = _fixture.BeginScope())
            {
                var db = before.Resolve<CodeSpaceDbContext>();
                var run = await db.AgentRun.AsNoTracking().SingleAsync(row => row.Id == runId, bounded.Token);
                run.Status.ShouldBe(AgentRunStatus.Running);
                run.RunnerHandleJson.ShouldBeNull("this case's fault fires before the handle write executes, so the run has no durable address to recover from");
                var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().Where(row => row.AgentRunId == runId).SingleAsync(bounded.Token);
                attempt.State.ShouldBe(HarnessProcessAttemptState.Running, "the durable attempt is the only thing still naming this execution");
                attempt.ClaimOwnerId.ShouldBeNull("the launch writes nothing onto the attempt: an observer claim taken here is unreleasable by either closer, which makes the attempt permanently unclosable");
                attemptId = attempt.Id;
            }
            (await new LocalProcessRunner().ProbeAsync(handle, bounded.Token)).State.ShouldBe(SandboxRunState.Running, "the recovery under test is only meaningful over a process that is actually alive");

            // The barrier the reconciler actually reads: a lapsed lease AND no recent events. Both are aged in the
            // real database rather than simulated, so the sweep reaches this run for the same reason it reaches any
            // other stale one.
            var stale = DateTimeOffset.UtcNow - AgentRunLiveness.Window - TimeSpan.FromMinutes(5);
            using (var age = _fixture.BeginScope())
            {
                var db = age.Resolve<CodeSpaceDbContext>();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET heartbeat_at = {stale}, lease_expires_at = {stale} WHERE id = {runId}", bounded.Token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_event SET occurred_at = {stale} WHERE agent_run_id = {runId}", bounded.Token);
            }

            // The capability adoption is feature-detected on. Asserted rather than assumed: without it the reconciler
            // falls back to the ordinary abandon, and this test would pass its way into a silent no-op.
            using (var capability = _fixture.BeginScope())
                capability.Resolve<CodeSpace.Core.Services.Agents.Capture.INativeRecordPlane>().ShouldBeAssignableTo<CodeSpace.Core.Services.Agents.Capture.INativeRecordLaunchPlane>("the durable process record plane must expose its launch sibling, or nothing can look up an admitted execution");

            // The protocol's two steps, asserted individually before the sweep that composes them — so a failure says
            // WHICH half broke instead of only that the run was abandoned.
            using (var probe = _fixture.BeginScope())
            {
                var plane = (CodeSpace.Core.Services.Agents.Capture.INativeRecordLaunchPlane)probe.Resolve<CodeSpace.Core.Services.Agents.Capture.INativeRecordPlane>();
                var admitted = (await plane.FindAdmittedLaunchAsync(runId, bounded.Token)).ShouldNotBeNull("the durable attempt is what still names this admitted execution");
                admitted.Identity.AttemptId.ShouldBe(attemptId, "the looked-up identity must be the live attempt's own");

                // The runner is resolved BY the kind the execution recorded, never by "whichever registered runner can
                // adopt at all" — a spool key is one backend's private layout, so the wrong runner would be handed an
                // address it cannot interpret.
                admitted.RunnerKind.ShouldBe(handle.Kind, "the admitted launch must name the runner that actually launched it");
                var adopter = probe.Resolve<ISandboxRunnerRegistry>().All.FirstOrDefault(runner => runner.Kind == admitted.RunnerKind).ShouldBeAssignableTo<ISandboxLaunchIdentityRunner>("the runner the execution names must be the one that can re-discover its launch");

                var rebuilt = (await adopter.AdoptAsync(new SandboxLaunchAdoption(admitted.SpoolKey, admitted.Identity), bounded.Token)).ShouldNotBeNull("the launch slot must yield its receipt to the exact identity that admitted it");
                rebuilt.ProcessId.ShouldBe(handle.ProcessId, "adoption must rebuild the address of the process that is running, never mint a new one");
            }

            using (var reconcile = _fixture.BeginScope())
                await reconcile.Resolve<IAgentRunReconcilerService>().ReconcileAsync(bounded.Token);

            using var verify = _fixture.BeginScope();
            var verifyDb = verify.Resolve<CodeSpaceDbContext>();
            var recovered = await verifyDb.AgentRun.AsNoTracking().SingleAsync(row => row.Id == runId, bounded.Token);
            var evidence = JsonSerializer.Serialize(new { recovered.Status, recovered.Error, HandleCommitted = recovered.RunnerHandleJson is not null, AttemptId = attemptId, LivePid = handle.ProcessId }, AgentJson.Options);

            recovered.Status.ShouldBe(AgentRunStatus.Running, "adoption restores the run's address; it does not decide the run's fate, and abandoning a live process here is the failure this slice removes; observed: " + evidence);
            var adopted = JsonSerializer.Deserialize<SandboxHandle>(recovered.RunnerHandleJson.ShouldNotBeNull("the reconciler must re-address the run from its admitted launch instead of blind-abandoning it; observed: " + evidence), AgentJson.Options).ShouldNotBeNull();
            adopted.ProcessId.ShouldBe(handle.ProcessId, "the adopted handle must name the process that is actually running, not a new one");
            var identity = adopted.NativeLaunch.ShouldNotBeNull("an adopted handle carries the immutable native reference it was rebuilt from").Identity.ShouldNotBeNull();
            identity.AttemptId.ShouldBe(attemptId, "the execution is adopted BY the durable attempt, so its identity must be that exact attempt");
            identity.AgentRunId.ShouldBe(runId);
            identity.TeamId.ShouldBe(teamId);

            (await new LocalProcessRunner().ProbeAsync(adopted, bounded.Token)).State.ShouldBe(SandboxRunState.Running, "the adopted address must resolve the same live process");

            // What the ADOPTED handle carries versus what the launch handle carried — the scope of a recovery, pinned
            // on the handle a future worker will actually read rather than on the one the dead worker built.
            adopted.ProgressLeaseDirectory.ShouldBe(LocalProcessRunner.ProgressLeaseDirectoryFor(runId),
                customMessage: "the progress lease is a pure function of the run id, so an adopted handle must carry the same lease the run's endpoint renews — a null one leaves the no-progress watchdog watching nothing");

            // And what it deliberately does NOT carry. These are pinned as ABSENT on purpose: each is unrecoverable
            // here, and half-restoring any of them would look like a recovery while behaving like a corruption.
            adopted.WorkspaceDirectory.ShouldBeNull("the base SHA this directory must be paired with is durable nowhere until the run's own result writes it, and the diff capture requires both — so an adopted run captures no diff rather than capturing one against an unknown base");
            adopted.WorkspaceBaseSha.ShouldBeNull("null exactly when WorkspaceDirectory is, which is the invariant the handle documents");
            adopted.InjectedKeyFingerprint.ShouldBeNull("the fingerprint comes from a decrypted credential the reconciler cannot decrypt, so an adopted run continues marker-only rather than re-tailing under a redactor it cannot prove it rebuilt");
            adopted.McpRunToken.ShouldBeNull("the run token is minted per launch and stamped by the write that failed, so an adopted run cannot re-open its MCP endpoint");
            adopted.McpSocketPath.ShouldBeNull("null exactly when McpRunToken is, which is what makes 'no fabric to re-open' one fact rather than two half-restored ones");
            adopted.AgentRunLogCaptureSessionId.ShouldBeNull("the session id is minted per launch and persisted only by the capture OPEN, which is downstream of the write that failed — recovering an earlier round's id would bind this handle to another spool's capture");

            Directory.Exists(handle.WorkspaceDirectory!).ShouldBeTrue("the adopted execution still owns its real clone, which now ages out through the workspace janitor rather than being reclaimed from the handle");

            // The fence, stated over the real slot: every other attempt identity is refused. Without this, adoption
            // would be a way for any run to claim a process another attempt admitted.
            var foreign = new SandboxLaunchAdoption(runId.ToString("N"), new SandboxLaunchIdentity(teamId, runId, Guid.NewGuid(), Guid.NewGuid()));
            var refused = await Should.ThrowAsync<NativeLaunchException>(() => new LocalProcessRunner().AdoptAsync(foreign, bounded.Token));
            refused.Reason.ShouldBe("binding-conflict", "a slot bound to one attempt must refuse every other, so a refusal is never mistaken for an absence");
        }
        finally
        {
            if (fault.Handle is { } leaked)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await new LocalProcessRunner().TerminateAsync(leaked, cleanup.Token);
                var stopped = await new LocalProcessRunner().ProbeAsync(leaked, cleanup.Token);
                stopped.State.ShouldNotBe(SandboxRunState.Running, "the test must not leak its live child");
                if (leaked.WorkspaceDirectory is { } workspace && Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
            }
        }
    }
}

public partial class AgentRunExecutorTests
{
    /// <summary>
    /// The regression this slice's first cut introduced, pinned on the ORDINARY path: a durable native launch with the
    /// record plane deployed must leave its process attempt CLOSABLE. Taking the attempt's 0137 observer claim at
    /// launch made every such attempt permanently unclosable — <c>ck_workflow_run_harness_process_attempt_terminal_claim</c>
    /// refuses a terminal state while a claim is held, no closer releases one, and both closers swallow their failure —
    /// so the run's process stayed Running for ever with no exit code and nothing went red. The launch therefore writes
    /// NOTHING onto the attempt, and this asserts the observable consequence rather than the absence of a statement.
    /// </summary>
    [Fact]
    public async Task Native_launch_leaves_its_process_attempt_closable()
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        Guid runId;
        using (var seed = await CodeSpace.IntegrationTests.Workflows.Infrastructure.WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId))
            runId = (await seed.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = "close", Harness = "scripted", Model = "test-model", TimeoutSeconds = 30 }, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;

        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // The container-resolved executor, deliberately not the hand-built one the other helpers use: only this one
        // carries the durable record plane, and the plane is the whole point of the assertion below.
        using (var execute = _fixture.BeginScope(builder => builder.RegisterInstance(new AgentHarnessRegistry([new ScriptedHarness("printf 'closed\\n'")])).As<IAgentHarnessRegistry>()))
        {
            execute.Resolve<CodeSpace.Core.Services.Agents.Capture.INativeRecordPlane>().ShouldBeAssignableTo<CodeSpace.Core.Services.Agents.Capture.INativeRecordLaunchPlane>("this test is only meaningful with the launch-side plane deployed");
            await execute.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, bounded.Token);
        }

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var run = await db.AgentRun.AsNoTracking().SingleAsync(row => row.Id == runId, bounded.Token);
        var attempt = await db.WorkflowRunHarnessProcessAttempt.AsNoTracking().Where(row => row.AgentRunId == runId).SingleAsync(bounded.Token);
        var evidence = JsonSerializer.Serialize(new { run.Status, run.Error, attempt.State, attempt.ExitCode, attempt.ClaimOwnerId, attempt.ClaimFence, attempt.ClaimExpiresAt }, AgentJson.Options);

        run.Status.ShouldBe(AgentRunStatus.Succeeded, "observed: " + evidence);
        attempt.State.ShouldBe(HarnessProcessAttemptState.Exited, "the observer saw this process exit, so the close must land — a launch-time write that leaves the row unclosable is the defect this pins; observed: " + evidence);
        attempt.ExitCode.ShouldBe(0, "observed: " + evidence);
        attempt.ClaimOwnerId.ShouldBeNull("nothing may hold this attempt's observer claim after a launch, because nothing on the closing paths releases one");
        attempt.ClaimExpiresAt.ShouldBeNull("the terminal-claim CHECK requires both claim columns released, so a lingering expiry is the same permanent block as a lingering owner");
        attempt.ClaimFence.ShouldBe(0, "the launch takes no claim, so its fence never advances");
    }
}

public partial class AgentRunExecutorTests
{
    /// <summary>
    /// The adopted handle is written under the SAME scanned identity the terminal CAS uses, so a worker that
    /// legitimately moved the run between this sweep's scan and its adoption write is never overwritten. Each case
    /// interposes ONE competing writer at exactly that instant — a second adopter that won the reclaim (fence), one
    /// that consumed a re-attach attempt, and one that already wrote its own address — and each corresponds to one
    /// clause of <c>AdoptCandidateHandleAsync</c>: deleting that clause makes its case go red.
    /// </summary>
    [Theory]
    [InlineData("fence")]
    [InlineData("reattach")]
    [InlineData("handle")]
    public async Task Adopted_handle_never_overwrites_a_run_another_worker_moved(string interloper)
    {
        if (OperatingSystem.IsWindows()) return;

        var teamId = await SeedTeamAsync();
        using var origin = new TempDir();
        await SeedLocalRepoAsync(origin.Path, "README.md", "launch-adoption-cas");
        var repositoryId = await SeedRepositoryAsync(teamId, new Uri(origin.Path).AbsoluteUri, "main");
        Guid runId;
        using (var seed = await CodeSpace.IntegrationTests.Workflows.Infrastructure.WorkflowsTestSeed.BeginSeedOperatorScopeAsync(_fixture, teamId))
            runId = (await seed.Resolve<IAgentRunService>().CreateAsync(new AgentTask { Goal = "edit", Harness = "scripted", Model = "test-model", RepositoryId = repositoryId, TimeoutSeconds = 120 }, teamId, null, null, iterationKey: "", cancellationToken: CancellationToken.None)).Id;
        var harness = new ScriptedHarness("printf 'started\n' > launch-effect; while :; do sleep 1; done");
        var fault = new RunnerHandleAcknowledgementFault(afterCommit: false);
        const int InterloperProcessId = 424242;
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            using (var execute = _fixture.BeginScope(builder =>
            {
                var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(fault).Options;
                builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
                builder.RegisterInstance(new AgentHarnessRegistry([harness])).As<IAgentHarnessRegistry>();
            }))
                await execute.Resolve<IAgentRunExecutor>().ExecuteAsync(runId, bounded.Token);

            var handle = fault.Handle.ShouldNotBeNull();
            (await new LocalProcessRunner().ProbeAsync(handle, bounded.Token)).State.ShouldBe(SandboxRunState.Running, "the race under test is only meaningful over a process adoption would really rebuild");

            var stale = DateTimeOffset.UtcNow - AgentRunLiveness.Window - TimeSpan.FromMinutes(5);
            using (var age = _fixture.BeginScope())
            {
                var db = age.Resolve<CodeSpaceDbContext>();
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET heartbeat_at = {stale}, lease_expires_at = {stale} WHERE id = {runId}", bounded.Token);
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run_event SET occurred_at = {stale} WHERE agent_run_id = {runId}", bounded.Token);
            }

            // The rival worker's own address — a REAL handle, serialized the same way the executor serializes one, so
            // the "already handled" case races a value the run could genuinely be carrying rather than a stub.
            var rival = handle with { ProcessId = InterloperProcessId };

            // The competing writer, landed on its OWN connection at the moment the adoption statement is about to
            // execute — which is precisely the window a scanned-then-written CAS exists to cover.
            var mutation = interloper switch
            {
                "fence" => "UPDATE agent_run SET fence_epoch = fence_epoch + 1 WHERE id = @id",
                "reattach" => "UPDATE agent_run SET reattach_attempts = reattach_attempts + 1 WHERE id = @id",
                "handle" => "UPDATE agent_run SET runner_handle = CAST(@handle AS jsonb) WHERE id = @id",
                _ => throw new ArgumentOutOfRangeException(nameof(interloper), interloper, "unknown interloper"),
            };
            var race = new AdoptionRace(_fixture.ConnectionString, runId, mutation, JsonSerializer.Serialize(rival, AgentJson.Options));

            using (var reconcile = _fixture.BeginScope(builder =>
            {
                var options = new DbContextOptionsBuilder<CodeSpaceDbContext>().UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().AddInterceptors(race).Options;
                builder.RegisterInstance(options).As<DbContextOptions<CodeSpaceDbContext>>().SingleInstance();
            }))
                await reconcile.Resolve<IAgentRunReconcilerService>().ReconcileAsync(bounded.Token);

            race.Fired.ShouldBeTrue("the adoption write must have been reached and raced; otherwise this case proves nothing about its CAS");

            using var verify = _fixture.BeginScope();
            var run = await verify.Resolve<CodeSpaceDbContext>().AgentRun.AsNoTracking().SingleAsync(row => row.Id == runId, bounded.Token);
            var evidence = JsonSerializer.Serialize(new { interloper, run.Status, run.Error, run.FenceEpoch, run.ReattachAttempts, run.RunnerHandleJson }, AgentJson.Options);

            run.Status.ShouldBe(AgentRunStatus.Running, "the sweep lost every CAS it attempted on a row that moved, so it must have decided nothing; observed: " + evidence);

            if (interloper == "handle")
                JsonSerializer.Deserialize<SandboxHandle>(run.RunnerHandleJson.ShouldNotBeNull(), AgentJson.Options).ShouldNotBeNull().ProcessId
                    .ShouldBe(InterloperProcessId, "the address the other worker wrote must survive: an adoption that overwrites a live worker's handle re-points the run at a process that worker is not observing; observed: " + evidence);
            else
                run.RunnerHandleJson.ShouldBeNull("the run's identity changed under the sweep, so the adopted handle must not land; observed: " + evidence);
        }
        finally
        {
            if (fault.Handle is { } leaked)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await new LocalProcessRunner().TerminateAsync(leaked, cleanup.Token);
                var stopped = await new LocalProcessRunner().ProbeAsync(leaked, cleanup.Token);
                stopped.State.ShouldNotBe(SandboxRunState.Running, "the test must not leak its live child");
                if (leaked.WorkspaceDirectory is { } workspace && Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
            }
        }
    }

    /// <summary>
    /// Lands one competing write on its own connection immediately before the reconciler's adoption UPDATE executes.
    /// A separate connection is required, not incidental: the intercepted statement takes its own <c>FOR UPDATE</c>
    /// lock, so the competitor has to commit BEFORE it runs — which is exactly the ordering the CAS is written for.
    /// </summary>
    private sealed class AdoptionRace(string connectionString, Guid runId, string mutationSql, string rivalHandleJson) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Fired || !command.CommandText.Contains("UPDATE agent_run AS target SET runner_handle = CAST(", StringComparison.Ordinal)) return result;

            Fired = true;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var mutate = new NpgsqlCommand(mutationSql, connection);
            mutate.Parameters.AddWithValue("id", runId);
            if (mutationSql.Contains("@handle", StringComparison.Ordinal)) mutate.Parameters.AddWithValue("handle", rivalHandleJson);
            (await mutate.ExecuteNonQueryAsync(cancellationToken)).ShouldBe(1, "the competing writer must actually move the row, or the race it stands for never happened");

            return result;
        }
    }
}
