using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Capture;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgentRunReconcilerOwnershipFlowTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("renew", "recover")]
    [InlineData("activate", "recover")]
    [InlineData("handle", "recover")]
    [InlineData("renew", "abandon")]
    [InlineData("activate", "abandon")]
    [InlineData("handle", "abandon")]
    [InlineData("handle", "reserve")]
    public async Task A_probe_of_a_frozen_candidate_cannot_terminalize_reserve_kill_or_invalidate_a_changed_run(string mutation, string outcome)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var runner = new PausedProbeRunner(outcome == "recover" ? SandboxRunState.Exited : SandboxRunState.Running);
        Guid runId;
        var handle = new SandboxHandle { Kind = runner.Kind, ProcessId = 1, SpoolDirectory = "/test/owned-probe", Deadline = DateTimeOffset.UtcNow.AddHours(1) };
        using (var setup = fixture.BeginScopeAs(userId, teamId))
        {
            var runs = setup.Resolve<IAgentRunService>();
            runId = (await runs.CreateAsync(new AgentTask { Goal = "probe ownership", Harness = "codex-cli", Model = "test" }, teamId, null, null, cancellationToken: CancellationToken.None)).Id;
            await runs.MarkRunningAsync(runId, CancellationToken.None);
            await runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);
            var attempts = outcome == "abandon" ? AgentRunReconcilerService.MaxReattachAttempts : 0;
            await setup.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour', reattach_attempts = {attempts} WHERE id = {runId}");
        }
        using var observer = fixture.BeginScope(builder => builder.RegisterInstance(new SandboxRunnerRegistry([runner])).As<ISandboxRunnerRegistry>());
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var sweep = observer.Resolve<IAgentRunReconcilerService>().ReconcileAsync(bounded.Token);
        try
        {
            await runner.Probed.Task.WaitAsync(bounded.Token);
            using var replacement = fixture.BeginScope();
            var runs = replacement.Resolve<IAgentRunService>();
            if (mutation == "renew") await runs.HeartbeatAsync(runId, bounded.Token);
            else if (mutation == "activate")
            {
                var reservation = (await runs.ReserveReattachAsync(runId, bounded.Token))!;
                (await runs.ActivateReattachAsync(reservation, bounded.Token)).ShouldNotBeNull();
            }
            else await runs.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle with { SpoolDirectory = "/test/replacement-process" }, AgentJson.Options), bounded.Token);
            var before = await runs.GetAsync(runId, bounded.Token);
            await replacement.Resolve<ICaptureIntentService>().OpenAsync(runId, teamId, null, before.FenceEpoch, "{}", bounded.Token);
            runner.Resume.SetResult();
            await sweep.WaitAsync(bounded.Token);
            var after = await runs.GetAsync(runId, bounded.Token);
            after.Status.ShouldBe(AgentRunStatus.Running, "the probe belongs to a superseded candidate; it cannot decide the current run's terminal state");
            after.OwnerId.ShouldBe(before.OwnerId);
            after.ReattachReservationId.ShouldBe(before.ReattachReservationId);
            after.FenceEpoch.ShouldBe(before.FenceEpoch);
            after.RunnerHandleJson.ShouldBe(before.RunnerHandleJson);
            after.LeaseExpiresAt.ShouldBe(before.LeaseExpiresAt);
            runner.Terminations.ShouldBe(0);
            (await replacement.Resolve<CodeSpaceDbContext>().CaptureIntent.AsNoTracking().SingleAsync(c => c.AgentRunId == runId)).Status.ShouldBe(CaptureIntentStatus.Intended);
        }
        finally { runner.Resume.TrySetResult(); bounded.Cancel(); try { await sweep; } catch { } }
    }

    private sealed class PausedProbeRunner(SandboxRunState state) : ISandboxRunner, ISandboxDurableRunner
    {
        public string Kind => "ownership-barrier-probe";
        public TaskCompletionSource Probed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Terminations { get; private set; }
        public async Task<SandboxProbe> ProbeAsync(SandboxHandle handle, CancellationToken cancellationToken)
        {
            Probed.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return new SandboxProbe { State = state, ExitCode = state == SandboxRunState.Exited ? 0 : null };
        }
        public Task TerminateAsync(SandboxHandle handle, CancellationToken cancellationToken) { Terminations++; return Task.CompletedTask; }
        public Task<SandboxHandle> LaunchAsync(SandboxSpec spec, string runKey, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SandboxResult> AttachAsync(SandboxHandle handle, Func<SandboxOutputFrame, CancellationToken, Task> onStdoutFrame, CancellationToken cancellationToken, Func<long, CancellationToken, Task>? onCheckpoint = null) => throw new NotSupportedException();
        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
