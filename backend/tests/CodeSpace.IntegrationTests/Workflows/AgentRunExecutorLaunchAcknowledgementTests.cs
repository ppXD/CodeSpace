using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
