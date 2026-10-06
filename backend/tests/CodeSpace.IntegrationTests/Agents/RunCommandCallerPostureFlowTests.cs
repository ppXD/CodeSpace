using System.Collections.Concurrent;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Tools;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// The two lanes into <c>agent.run_command</c>, side by side over the production registrations: an AGENT's call
/// through the real MCP tool dispatch (<c>McpRequestHandler</c> → <c>NodeAgentTool</c> → the node →
/// <c>RunCommandService</c>), and a WORKFLOW NODE run by the real engine. The agent's command must run no wider than the
/// agent's own run — a network-off caller that asks for <c>"network": true</c> gets a severed sandbox and its tier's
/// ceilings — while the workflow node's authored network is exactly what it was.
///
/// <para>Tier 🟡 medium-mock: every production class runs for real, including the real <see cref="LocalProcessRunner"/>
/// that executes the command; the only stand-in is a recording decorator on that runner, because the posture is a
/// property of the spec it receives and a macOS / unconfinable host cannot show a severed namespace. The spec it
/// recorded is then run through the production child chain (<see cref="LocalProcessRunner.ChildCommand"/>) with a
/// stand-in bwrap path, so the assertion is the argv a confining Linux worker would exec. The real-kernel proof of
/// <c>--unshare-net</c> itself belongs to the Linux sandbox lane.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunCommandCallerPostureFlowTests(PostgresFixture fixture)
{
    private const string FakeBwrap = "/usr/bin/bwrap";

    [Fact]
    public async Task A_network_off_agent_asking_run_command_for_the_network_gets_a_severed_ceilinged_sandbox_through_the_real_tool_dispatch()
    {
        if (OperatingSystem.IsWindows()) return;

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var runner = new RecordingRunner();
        using var scope = ScopeRunningCommandsOn(runner);

        // Unleashed so the destructive tool is gate-Allowed and needs no approval; the run's OWN permissions keep the
        // network off — the posture an author pins with an agent.run node's network override.
        var handler = new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Unleashed, teamId, permissions: new AgentPermissions { Network = AgentNetworkAccess.Off });

        var result = await CallToolAsync(handler, "agent.run_command", new { command = "true", network = true });

        result.GetProperty("isError").GetBoolean().ShouldBeFalse(customMessage: $"the command must still run — narrowed, not refused: {result.GetRawText()}");
        var spec = runner.Specs.ShouldHaveSingleItem("the tool call must reach the runner exactly once");
        spec.AllowNetwork.ShouldBeFalse("a network-off run cannot open a network by asking a tool for one");
        spec.MaxMemoryMb.ShouldBe(6144, "the command is held to the calling run's Unleashed memory row");
        spec.MaxCpuPercent.ShouldBe(400);
        Chain(spec).ShouldContain("--unshare-net", customMessage: $"a confining worker must sever the command's network: [{string.Join(' ', Chain(spec))}]");
        result.GetRawText().ShouldContain("networkNarrowed", customMessage: "the tool result tells the agent its run's posture took the network it asked for");
    }

    [Theory]
    [InlineData(true, 1)]    // two connections of ONE run: the run's commands queue
    [InlineData(false, 2)]   // two runs: never queued behind each other
    public async Task Overlapping_commands_from_one_run_never_run_at_once_through_the_real_tool_dispatch(bool sameRun, int expectedMaxConcurrent)
    {
        // Each command gets a cgroup leaf of its own carrying the run's whole tier row. The run token in the agent's
        // config lets it open as many endpoint connections as it likes, each with its own handler — so a run that
        // starts two commands at once would hold two rows. Queued per run, its commands never hold more than one.
        if (OperatingSystem.IsWindows()) return;

        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var runner = new OverlapRunner(expectedConcurrency: 2);
        using var scope = fixture.BeginScope(builder => builder.RegisterInstance(new SandboxRunnerRegistry([runner])).As<ISandboxRunnerRegistry>());
        var firstRun = Guid.NewGuid();
        var connections = new[] { firstRun, sameRun ? firstRun : Guid.NewGuid() }
            .Select(runId => new McpRequestHandler(scope.Resolve<IAgentToolRegistry>(), AgentAutonomyLevel.Unleashed, teamId, runId: runId))
            .ToList();

        var results = await Task.WhenAll(connections.Select(handler => CallToolAsync(handler, "agent.run_command", new { command = "true" })));

        results.ShouldAllBe(result => !result.GetProperty("isError").GetBoolean(), customMessage: "fixture check: both commands ran");
        runner.Started.ShouldBe(2);
        runner.MaxConcurrent.ShouldBe(expectedMaxConcurrent, sameRun ? "one run's commands must never run at the same time" : "another run's command is not queued behind this one");
    }

    [Fact]
    public async Task A_workflow_node_asking_for_the_network_keeps_it_through_the_real_engine()
    {
        if (OperatingSystem.IsWindows()) return;

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(fixture, workflowId, teamId);
        var runner = new RecordingRunner();

        using (var scope = ScopeRunningCommandsOn(runner))
            await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);

        using (var verify = fixture.BeginScope())
        {
            var node = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().SingleAsync(candidate => candidate.RunId == runId && candidate.NodeId == "command");
            node.Status.ShouldBe(NodeStatus.Success, node.Error);
        }

        var spec = runner.Specs.ShouldHaveSingleItem("the engine must run the node's command exactly once");
        spec.AllowNetwork.ShouldBeTrue("a workflow node has no calling run: its authored network stands, as it always did");
        spec.MaxMemoryMb.ShouldBe(0, "no caller ceilings on the workflow lane — unchanged");
        spec.MaxCpuPercent.ShouldBe(0);
        spec.EgressAllowlist.ShouldBeNull();
        Chain(spec).ShouldNotContain("--unshare-net", customMessage: $"the workflow node's command keeps the host network: [{string.Join(' ', Chain(spec))}]");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A scope whose "local" sandbox runner is <paramref name="runner"/>; everything else is the production registration, and child scopes (the tool's node invocation, the engine's) inherit it.</summary>
    private ILifetimeScope ScopeRunningCommandsOn(RecordingRunner runner) =>
        fixture.BeginScope(builder => builder.RegisterInstance(new SandboxRunnerRegistry([runner])).As<ISandboxRunnerRegistry>());

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var author = fixture.BeginScopeAs(userId, teamId, Roles.Admin);

        return await author.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "networked-command", Enabled = true, Activations = new List<WorkflowActivationInput>(),
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "command", TypeKey = "agent.run_command", Config = WorkflowsTestSeed.EmptyJson(), Inputs = JsonSerializer.SerializeToElement(new { command = "true", network = true }) },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition> { new() { From = "start", To = "command" }, new() { From = "command", To = "end" } },
            },
        });
    }

    private static async Task<JsonElement> CallToolAsync(McpRequestHandler handler, string name, object arguments)
    {
        var request = JsonSerializer.SerializeToElement(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name, arguments } });

        var response = await handler.HandleAsync(request, CancellationToken.None);

        return response!.Value.GetProperty("result");
    }

    /// <summary>The production child chain for <paramref name="spec"/> on a host whose bwrap is at <see cref="FakeBwrap"/> — what a confining worker execs.</summary>
    private static IReadOnlyList<string> Chain(SandboxSpec spec) =>
        LocalProcessRunner.ChildCommand(new LocalProcessRunner.CommandIsolationContext(spec, null, null, Array.Empty<string>(), Array.Empty<string>()), FakeBwrap, prlimit: null);

    /// <summary>Records how many commands run at once; each waits until <paramref name="expectedConcurrency"/> run together or a short grace elapses, so commands that can overlap always do.</summary>
    private sealed class OverlapRunner(int expectedConcurrency) : ISandboxRunner
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;

        public int Started { get; private set; }
        public int MaxConcurrent { get; private set; }
        public string Kind => LocalProcessRunner.LocalKind;

        public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Started++;
                _running++;
                MaxConcurrent = Math.Max(MaxConcurrent, _running);
                if (_running >= expectedConcurrency) _all.TrySetResult();
            }

            await Task.WhenAny(_all.Task, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));

            lock (_gate) _running--;

            return new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" };
        }
    }

    /// <summary>Records every spec it is handed, then runs it on the real local runner.</summary>
    private sealed class RecordingRunner : ISandboxRunner
    {
        private readonly LocalProcessRunner _real = new();

        public ConcurrentQueue<SandboxSpec> Specs { get; } = new();

        public string Kind => LocalProcessRunner.LocalKind;

        public Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            Specs.Enqueue(spec);

            return _real.RunAsync(spec, cancellationToken);
        }
    }
}
