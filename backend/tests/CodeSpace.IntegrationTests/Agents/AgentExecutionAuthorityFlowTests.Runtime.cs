using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

public sealed partial class AgentExecutionAuthorityFlowTests
{
    [Fact]
    public async Task Publisher_service_stamps_the_immutable_author_and_admission_persists_both_subjects()
    {
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowVersion.AsNoTracking().SingleAsync(v => v.WorkflowId == seed.WorkflowId)).CreatedBy.ShouldBe(seed.UserId);
        var runId = await scope.Resolve<IRunStarter>().StartAsync(Manual(seed), CancellationToken.None);
        var receipt = JsonSerializer.Deserialize<AgentExecutionAuthority>((await db.WorkflowRunExecutionAuthority.AsNoTracking().SingleAsync(r => r.WorkflowRunId == runId)).ReceiptJson, AgentJson.Options)!;
        receipt.LogicalRunId.ShouldBe(runId);
        receipt.Subjects.Select(s => s.Kind).ShouldBe(new[] { "author", "launcher" });
        receipt.Subjects.ShouldAllBe(s => s.UserId == seed.UserId && s.MembershipId != null);
        await Should.ThrowAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE workflow_run_execution_authority SET receipt_json = '{{}}'::jsonb WHERE workflow_run_id = {runId}"));
    }

    [Fact]
    public async Task Admission_failure_does_not_leave_a_session_or_request_to_commit_later()
    {
        var seed = await SeedAsync();
        await RevokeAsync(seed);
        using var scope = _fixture.BeginScope();
        await Should.ThrowAsync<Exception>(() => scope.Resolve<IRunStarter>().StartAsync(Manual(seed), CancellationToken.None));
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.SaveChangesAsync();
        (await db.WorkSession.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(0);
        (await db.WorkflowRunRequest.CountAsync(r => r.TeamId == seed.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Snapshot_launch_ceiling_intersects_an_unleashed_agent_and_replaces_a_forged_receipt()
    {
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var definition = WorkflowsTestSeed.MinimalDefinition() with { LaunchContract = new TaskLaunchContract { Version = 1, Goal = "bounded task", SurfaceKind = "chat", ResolvedRoute = new RoutePlan { ProjectionKind = "single-agent", Caps = new RouteCaps { AutonomyCeiling = "Standard" } }, RequestedControls = new TaskLaunchControls { Autonomy = "Standard" } } };
        var runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(definition, seed.TeamId, seed.UserId, "{}", [], "single-agent", null, CancellationToken.None);
        var forged = new AgentExecutionAuthority { Version = 999, PolicyVersion = "forged", TeamId = seed.TeamId, LogicalRunId = runId, SourceKind = "forged", DefinitionHash = "", GrantedCeiling = AgentAutonomyLevel.Unleashed, IssuedAt = DateTimeOffset.UtcNow, Subjects = [] };
        var agent = await scope.Resolve<IAgentRunService>().CreateAsync(Task() with { ExecutionAuthority = forged }, seed.TeamId, runId, "agent", cancellationToken: CancellationToken.None);
        var task = JsonSerializer.Deserialize<AgentTask>(agent.TaskJson, AgentJson.Options)!;
        task.Autonomy.ShouldBe(AgentAutonomyLevel.Standard);
        task.Permissions.Network.ShouldBe(AgentNetworkAccess.Off);
        task.ExecutionAuthority!.Version.ShouldBe(1);
        task.ExecutionAuthority.GrantedCeiling.ShouldBe(AgentAutonomyLevel.Standard);
    }

    [Theory]
    [InlineData("config")]
    [InlineData("publisher")]
    [InlineData("revision")]
    public async Task Trigger_admission_rejects_forged_or_stale_server_snapshot(string field)
    {
        var seed = await SeedAsync();
        var source = await TriggerAsync(seed);
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        if (field == "config") source = source with { ActivationSnapshotJson = source.ActivationSnapshotJson!.Replace("\"cron\":\"0 * * * *\"", "\"cron\":\"* * * * *\"") };
        if (field == "publisher") source = source with { ActivationSnapshotJson = source.ActivationSnapshotJson!.Replace(seed.UserId.ToString(), Guid.NewGuid().ToString()) };
        if (field == "revision") await db.WorkflowActivation.Where(a => a.Id == source.ActivationId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Enabled, true));
        var exception = await Should.ThrowAsync<Exception>(() => scope.Resolve<IRunStarter>().StartAsync(source, CancellationToken.None));
        AssertAuthorityFailure(exception);
    }

    [Fact]
    public async Task Disabling_a_trigger_stops_new_admission_but_does_not_revoke_its_already_admitted_run()
    {
        var seed = await SeedAsync();
        var source = await TriggerAsync(seed);
        Guid runId;
        using (var scope = _fixture.BeginScope()) runId = await scope.Resolve<IRunStarter>().StartAsync(source, CancellationToken.None);
        using var action = _fixture.BeginScope();
        await action.Resolve<CodeSpaceDbContext>().WorkflowActivation.Where(a => a.Id == source.ActivationId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Enabled, false));
        var agent = await action.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, runId, "agent", cancellationToken: CancellationToken.None);
        (await action.Resolve<IAgentRunService>().MarkRunningAsync(agent.Id, CancellationToken.None)).ShouldBe(1);
        AssertAuthorityFailure(await Should.ThrowAsync<Exception>(() => action.Resolve<IRunStarter>().StartAsync(source, CancellationToken.None)));
    }

    [Theory]
    [InlineData("membership")]
    [InlineData("role")]
    [InlineData("account")]
    [InlineData("cancel")]
    public async Task A_persistent_real_Mcp_connection_refuses_its_second_call_after_revocation(string revocation)
    {
        var seed = await SeedAsync();
        Guid runId;
        using (var create = _fixture.BeginScopeAs(seed.UserId, seed.TeamId)) runId = (await create.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, null, null, cancellationToken: CancellationToken.None)).Id;
        using var host = _fixture.BeginScope();
        var tool = new CountingTool();
        var socketPath = $"/tmp/cs-authority-{Guid.NewGuid():N}.sock";
        const string token = "test-authority-token";
        await using var endpoint = new AgentMcpEndpoint(runId, new ToolRegistry(tool), AgentAutonomyLevel.Unleashed, seed.TeamId, SecretRedactor.None, socketPath, token, host.Resolve<IAgentMcpConnectRegistry>(), host.Resolve<IServiceScopeFactory>().CreateScope(), CancellationToken.None, NullLogger.Instance, governanceEnabled: true);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false));
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(token);
        var first = await CallAsync(reader, writer, 1);
        first.GetProperty("isError").GetBoolean().ShouldBeFalse();
        using (var revoke = _fixture.BeginScope())
        {
            var db = revoke.Resolve<CodeSpaceDbContext>();
            if (revocation == "membership") await db.TeamMembership.Where(m => m.TeamId == seed.TeamId && m.UserId == seed.UserId).ExecuteDeleteAsync();
            if (revocation == "role") await db.TeamMembership.Where(m => m.TeamId == seed.TeamId && m.UserId == seed.UserId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, TeamRole.Viewer));
            if (revocation == "account") await db.User.Where(u => u.Id == seed.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.DeactivatedAt, DateTimeOffset.UtcNow));
            if (revocation == "cancel") await db.AgentRun.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, AgentRunStatus.Cancelled));
        }
        var second = await CallAsync(reader, writer, 2);
        second.GetProperty("isError").GetBoolean().ShouldBeTrue();
        second.GetProperty("structuredContent").GetProperty("code").GetString().ShouldBe("agent.authority_denied");
        second.GetProperty("structuredContent").GetProperty("retryable").GetBoolean().ShouldBeFalse();
        tool.Calls.ShouldBe(1);
        endpoint.ObservedToolCalls.ShouldBe(2);
        (await host.Resolve<IAgentRunService>().GetEventsAsync(runId, seed.TeamId, 0, CancellationToken.None)).ShouldContain(e => e.DataJson != null && e.DataJson.Contains("authority.denied"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revoked_executor_claim_or_reattach_lands_terminal_and_resumes_the_real_parent_wait(bool reattach)
    {
        var seed = await SeedAsync();
        Guid workflowRunId, agentRunId;
        using (var create = _fixture.BeginScopeAs(seed.UserId, seed.TeamId))
        {
            workflowRunId = await create.Resolve<IRunStarter>().StartAsync(Manual(seed), CancellationToken.None);
            agentRunId = (await create.Resolve<IAgentRunService>().CreateAsync(Task(), seed.TeamId, workflowRunId, "agent", cancellationToken: CancellationToken.None)).Id;
            if (reattach) await create.Resolve<IAgentRunService>().MarkRunningAsync(agentRunId, CancellationToken.None);
        }
        using (var park = _fixture.BeginScope())
        {
            var db = park.Resolve<CodeSpaceDbContext>();
            await db.WorkflowRun.Where(r => r.Id == workflowRunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Suspended));
            db.WorkflowRunWait.Add(new WorkflowRunWait { Id = Guid.NewGuid(), RunId = workflowRunId, NodeId = "agent", WaitKind = WorkflowWaitKinds.AgentRun, Token = agentRunId.ToString(), CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        await RevokeAsync(seed);
        using (var execute = _fixture.BeginScope())
        {
            var executor = execute.Resolve<IAgentRunExecutor>();
            if (reattach)
            {
                await execute.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {agentRunId}");
                var reservation = (await execute.Resolve<IAgentRunService>().ReserveReattachAsync(agentRunId, CancellationToken.None))!;
                await executor.ReattachAsync(reservation, CancellationToken.None);
            }
            else await executor.ExecuteAsync(agentRunId, CancellationToken.None);
            // Duplicate job delivery exits normally; it neither starts a harness nor retries forever.
            await executor.ExecuteAsync(agentRunId, CancellationToken.None);
        }
        using var verify = _fixture.BeginScope();
        var verifyDb = verify.Resolve<CodeSpaceDbContext>();
        var agent = await verifyDb.AgentRun.AsNoTracking().SingleAsync(r => r.Id == agentRunId);
        agent.Status.ShouldBe(AgentRunStatus.Failed);
        JsonSerializer.Deserialize<AgentRunResult>(agent.ResultJson!, AgentJson.Options)!.ExitReason.ShouldBe("authority-denied");
        var wait = await verifyDb.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == workflowRunId);
        wait.Status.ShouldBe(WorkflowWaitStatuses.Resolved);
        wait.PayloadJson.ShouldContain("authority-denied");
        (await verifyDb.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == workflowRunId)).Status.ShouldBe(WorkflowRunStatus.Enqueued);
    }

    [Fact]
    public async Task A_revoked_reattach_terminates_the_actual_detached_process_after_winning_the_terminal_cas()
    {
        var seed = await SeedAsync();
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var service = scope.Resolve<IAgentRunService>();
        var runId = (await service.CreateAsync(Task(), seed.TeamId, null, null, cancellationToken: CancellationToken.None)).Id;
        await service.MarkRunningAsync(runId, CancellationToken.None);
        var runner = (ISandboxDurableRunner)scope.Resolve<ISandboxRunnerRegistry>().Resolve(LocalProcessRunner.LocalKind);
        var handle = await runner.LaunchAsync(new SandboxSpec { Command = "/bin/sh", Args = ["-c", "sleep 45"], TimeoutSeconds = 60 }, runId.ToString("N"), CancellationToken.None);
        try
        {
            await service.SetRunnerHandleAsync(runId, JsonSerializer.Serialize(handle, AgentJson.Options), CancellationToken.None);
            (await runner.ProbeAsync(handle, CancellationToken.None)).State.ShouldBe(SandboxRunState.Running);
            await RevokeAsync(seed);

            using var worker = _fixture.BeginScope();
            await worker.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE agent_run SET lease_expires_at = clock_timestamp() - interval '1 hour' WHERE id = {runId}");
            var reservation = (await worker.Resolve<IAgentRunService>().ReserveReattachAsync(runId, CancellationToken.None))!;
            await worker.Resolve<IAgentRunExecutor>().ReattachAsync(reservation, CancellationToken.None);

            using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while ((await runner.ProbeAsync(handle, bound.Token)).State == SandboxRunState.Running) await System.Threading.Tasks.Task.Delay(25, bound.Token);
            (await worker.Resolve<IAgentRunService>().GetAsync(runId, CancellationToken.None)).Status.ShouldBe(AgentRunStatus.Failed);
        }
        finally
        {
            await runner.TerminateAsync(handle, CancellationToken.None);
            if (Directory.Exists(handle.SpoolDirectory)) Directory.Delete(handle.SpoolDirectory, true);
        }
    }

    private async Task<RunSourceEnvelope> TriggerAsync(Seed seed)
    {
        using var scope = _fixture.BeginScopeAs(seed.UserId, seed.TeamId);
        var db = scope.Resolve<CodeSpaceDbContext>();
        var activation = new WorkflowActivation { Id = Guid.NewGuid(), WorkflowId = seed.WorkflowId, TypeKey = "trigger.schedule", ConfigJson = "{\"cron\":\"0 * * * *\"}", Enabled = true };
        db.WorkflowActivation.Add(activation);
        await db.SaveChangesAsync();
        activation = await db.WorkflowActivation.AsNoTracking().Include(a => a.Workflow).SingleAsync(a => a.Id == activation.Id);
        return Manual(seed) with { SourceType = WorkflowRunSourceTypes.ScheduleCron, ActorType = WorkflowRunActorTypes.System, ActorId = SystemUsers.SeederId, ActivationId = activation.Id, ActivationSnapshotJson = ActivationAuthoritySnapshot.Serialize(activation), CreatedBy = SystemUsers.SeederId };
    }

    private static async Task<JsonElement> CallAsync(StreamReader reader, StreamWriter writer, int id)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name = "test.read", arguments = new { id } } }));
        return JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!).RootElement.GetProperty("result").Clone();
    }

    private sealed class ToolRegistry : IAgentToolRegistry
    {
        private readonly IAgentTool _tool;
        public ToolRegistry(IAgentTool tool) => _tool = tool;
        public IReadOnlyList<IAgentTool> All => [_tool];
        public IAgentTool? Resolve(string kind) => kind == _tool.Kind ? _tool : null;
    }

    private sealed class CountingTool : IAgentTool
    {
        public int Calls { get; private set; }
        public string Kind => "test.read";
        public string Description => "Count authorized calls";
        public JsonElement InputSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public JsonElement OutputSchema => JsonSerializer.SerializeToElement(new { type = "object" });
        public bool IsReadOnly => true;
        public bool IsDestructive => false;
        public AgentToolValidation ValidateInput(JsonElement input) => AgentToolValidation.Valid;
        public Task<AgentToolResult> CallAsync(AgentToolCall call, CancellationToken cancellationToken) { Calls++; return System.Threading.Tasks.Task.FromResult(AgentToolResult.Ok(JsonSerializer.SerializeToElement(new { calls = Calls }), 10)); }
    }
}
