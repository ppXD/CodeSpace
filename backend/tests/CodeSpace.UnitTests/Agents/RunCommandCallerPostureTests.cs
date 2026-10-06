using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Commands;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// <c>agent.run_command</c> called by an AGENT runs no wider than that agent's own run. The command's sandbox is a
/// sandbox of its own — the tier clamps that bound the agent's spec never reach it — so without the caller's posture
/// a network-off agent could hand itself the internet by asking a tool for <c>"network": true</c>, and every command it
/// ran escaped the cgroup ceilings its own run is held to. Pinned at <see cref="RunCommandService.BuildSpec"/> (the one
/// request → spec projection) and then through the production child chain
/// (<see cref="LocalProcessRunner.ChildCommand"/>) with a stand-in bwrap, so the severed network is proven as the argv
/// a confining host would exec, on any host. A workflow node — no caller — keeps exactly the posture it had.
/// </summary>
[Trait("Category", "Unit")]
public class RunCommandCallerPostureTests
{
    private const string FakeBwrap = "/usr/bin/bwrap";

    // ── The matrix: caller tier × deployment ceiling ──────────────────────────

    [Theory]
    // caller tier                    deployment   network  memory  cpu
    [InlineData(AgentAutonomyLevel.Confined, null, false, 1024, 100)]
    [InlineData(AgentAutonomyLevel.Confined, "Trusted", false, 1024, 100)]
    [InlineData(AgentAutonomyLevel.Confined, "Standard", false, 1024, 100)]
    [InlineData(AgentAutonomyLevel.Confined, "Confined", false, 1024, 100)]
    [InlineData(AgentAutonomyLevel.Standard, null, false, 4096, 400)]
    [InlineData(AgentAutonomyLevel.Standard, "Trusted", false, 4096, 400)]
    [InlineData(AgentAutonomyLevel.Standard, "Standard", false, 4096, 400)]
    [InlineData(AgentAutonomyLevel.Standard, "Confined", false, 1024, 100)]
    [InlineData(AgentAutonomyLevel.Trusted, null, true, 6144, 400)]
    [InlineData(AgentAutonomyLevel.Trusted, "Trusted", true, 6144, 400)]
    [InlineData(AgentAutonomyLevel.Trusted, "Standard", false, 4096, 400)]
    [InlineData(AgentAutonomyLevel.Trusted, "Confined", false, 1024, 100)]
    [InlineData(AgentAutonomyLevel.Unleashed, null, true, 6144, 400)]
    [InlineData(AgentAutonomyLevel.Unleashed, "Trusted", true, 6144, 400)]
    [InlineData(AgentAutonomyLevel.Unleashed, "Standard", false, 4096, 400)]
    [InlineData(AgentAutonomyLevel.Unleashed, "Confined", false, 1024, 100)]
    public void An_agent_called_command_runs_no_wider_than_its_callers_tier_under_the_deployment_ceiling(AgentAutonomyLevel caller, string? deployment, bool expectedNetwork, int expectedMemoryMb, int expectedCpuPercent)
    {
        var spec = WithSettings(deployment, hostMemoryBudgetMb: null, () => RunCommandService.BuildSpec(AgentAsks(PostureOf(caller)), workingDirectory: null));

        spec.AllowNetwork.ShouldBe(expectedNetwork, $"a {caller} caller under deployment ceiling '{deployment ?? "(unset)"}' asking for the network must reach the runner with AllowNetwork={expectedNetwork}");
        spec.MaxMemoryMb.ShouldBe(expectedMemoryMb, $"a {caller} caller's command is held to its tier's memory row, clamped by the deployment ceiling '{deployment ?? "(unset)"}'");
        spec.MaxCpuPercent.ShouldBe(expectedCpuPercent);
        spec.EgressAllowlist.ShouldBeNull("a caller with full egress narrows nothing to an allowlist");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Trusted", true)]
    [InlineData("Standard", false)]
    public void A_workflow_node_command_keeps_its_authored_posture_under_the_deployment_ceiling_alone(string? deployment, bool expectedNetwork)
    {
        var spec = WithSettings(deployment, hostMemoryBudgetMb: null, () => RunCommandService.BuildSpec(new RunCommandRequest { Command = "curl", AllowNetwork = true }, workingDirectory: null));

        spec.AllowNetwork.ShouldBe(expectedNetwork, "no calling run: the authored network flag under the deployment ceiling, exactly as before");
        spec.MaxMemoryMb.ShouldBe(0, "a workflow node's command carries no caller ceiling — unchanged");
        spec.MaxCpuPercent.ShouldBe(0);
        spec.EgressAllowlist.ShouldBeNull();
    }

    // ── The run's own permissions, not just its tier ──────────────────────────

    [Fact]
    public void A_caller_whose_own_network_is_off_cannot_reach_the_network_through_a_command_whatever_its_tier()
    {
        // An Unleashed run whose author pinned network off: the agent's own sandbox is severed, so the command it asks
        // for must be too — the tier alone would have granted it.
        var caller = new AgentRunPosture { Autonomy = AgentAutonomyLevel.Unleashed, Permissions = new AgentPermissions { Network = AgentNetworkAccess.Off } };

        var spec = WithSettings(null, hostMemoryBudgetMb: null, () => RunCommandService.BuildSpec(AgentAsks(caller), workingDirectory: null));

        spec.AllowNetwork.ShouldBeFalse("a command an agent asks for never has a network its caller does not");
        spec.MaxMemoryMb.ShouldBe(6144, "the ceilings still follow the caller's tier");
    }

    [Fact]
    public void A_command_that_did_not_ask_for_the_network_gets_none_from_a_networked_caller()
    {
        var spec = WithSettings(null, hostMemoryBudgetMb: null, () => RunCommandService.BuildSpec(new RunCommandRequest { Command = "make", AllowNetwork = false, CallerPosture = PostureOf(AgentAutonomyLevel.Unleashed) }, workingDirectory: null));

        spec.AllowNetwork.ShouldBeFalse("the caller's posture only ever narrows: a command that asked for no network gets none");
    }

    [Theory]
    [InlineData(new[] { " Registry.NPMjs.org " }, true, new[] { "registry.npmjs.org" })]
    [InlineData(new string[0], false, null)]
    public void An_allowlisted_caller_hands_its_command_only_the_operator_named_hosts_and_severs_it_when_there_are_none(string[] extraHosts, bool expectedNetwork, string[]? expectedAllowlist)
    {
        // The run's own allowlist adds its model host and its repositories' git hosts; a command needs neither, and a
        // repository the command names may sit on a host the run never had. Only the operator's extra hosts are a
        // strict subset of what the run may reach, so they are all the command gets — and none means severed, never
        // full egress.
        var caller = new AgentRunPosture { Autonomy = AgentAutonomyLevel.Trusted, Permissions = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist, EgressAllowHosts = extraHosts } };

        var spec = WithSettings(null, hostMemoryBudgetMb: null, () => RunCommandService.BuildSpec(AgentAsks(caller), workingDirectory: null));

        spec.AllowNetwork.ShouldBe(expectedNetwork);
        spec.EgressAllowlist.ShouldBe(expectedAllowlist);
    }

    [Fact]
    public void The_operators_host_memory_budget_narrows_an_agent_called_commands_ceiling()
    {
        var spec = WithSettings(null, hostMemoryBudgetMb: 512, () => RunCommandService.BuildSpec(AgentAsks(PostureOf(AgentAutonomyLevel.Trusted)), workingDirectory: null));

        spec.MaxMemoryMb.ShouldBe(512, "the same host budget that narrows the agent's own run narrows the command it asks for");
        spec.MaxCpuPercent.ShouldBe(400);
    }

    // ── The argv a confining host execs ───────────────────────────────────────

    [Theory]
    [InlineData("workflow-node", false)]       // the authored network, unchanged: the host network is shared
    [InlineData("networked-agent", false)]     // a caller with network passes its network through
    [InlineData("network-off-agent", true)]    // the bypass this closes: the caller's own severed network holds
    [InlineData("allowlisted-agent", true)]    // an allowlist bwrap cannot enforce outside a filtered namespace severs
    public void Under_bubblewrap_the_command_chain_severs_exactly_the_commands_whose_caller_has_no_open_network(string lane, bool expectedSevered)
    {
        var spec = WithSettings(null, hostMemoryBudgetMb: null, () => RunCommandService.BuildSpec(RequestFor(lane), workingDirectory: null));

        var argv = LocalProcessRunner.ChildCommand(new LocalProcessRunner.CommandIsolationContext(spec, null, null, Array.Empty<string>(), Array.Empty<string>()), FakeBwrap, prlimit: null);

        argv[0].ShouldBe(FakeBwrap, "a confining host runs the command inside bubblewrap");
        argv.Contains("--unshare-net").ShouldBe(expectedSevered, $"the {lane} command's chain must {(expectedSevered ? "" : "not ")}carry --unshare-net: [{string.Join(' ', argv)}]");
    }

    [Fact]
    public void A_cgroup_host_caps_an_agent_called_command_at_its_callers_ceilings()
    {
        var spec = WithSettings(null, hostMemoryBudgetMb: null, () => RunCommandService.BuildSpec(RequestFor("network-off-agent"), workingDirectory: null));

        // The plan the command launch builds from the spec on a host with a delegated cgroup-v2 root.
        var plan = CgroupResourcePlan.Build("/sys/fs/cgroup/codespace", "command", spec.MaxMemoryMb, spec.MaxCpuPercent, maxPids: 0).ShouldNotBeNull("an agent's command is capped");

        plan.Limits.Single(limit => limit.FileName == "memory.max").Value.ShouldBe("6442450944", "the calling Unleashed run's memory row");
        plan.Limits.Single(limit => limit.FileName == "cpu.max").Value.ShouldBe("400000 100000", "and its cpu row");
    }

    [Fact]
    public void A_cgroup_host_leaves_a_workflow_nodes_command_uncapped()
    {
        var spec = WithSettings(null, hostMemoryBudgetMb: null, () => RunCommandService.BuildSpec(RequestFor("workflow-node"), workingDirectory: null));

        CgroupResourcePlan.Build("/sys/fs/cgroup/codespace", "command", spec.MaxMemoryMb, spec.MaxCpuPercent, maxPids: 0).ShouldBeNull("a workflow node's command asks for no cgroup at all — unchanged");
    }

    // ── What the agent is told when its command loses the network it asked for ─

    [Theory]
    [InlineData("workflow-node", null)]                 // no calling run: nothing narrowed it
    [InlineData("networked-agent", null)]               // granted as asked
    [InlineData("network-off-agent", "off: the calling run (Unleashed) has no network — severed only where the sandbox confines")]
    [InlineData("allowlisted-agent", "narrowed to the calling run's egress allowlist (registry.npmjs.org)")]
    [InlineData("empty-allowlist-agent", "off: the calling run's egress allowlist names no host a command may reach — severed only where the sandbox confines")]
    [InlineData("quiet-agent", null)]                   // a command that asked for no network lost nothing
    public void The_command_says_when_its_callers_posture_took_the_network_it_asked_for(string lane, string? expected)
    {
        var notice = WithSettings(null, hostMemoryBudgetMb: null, () => RunCommandService.CallerNetworkNarrowing(RequestFor(lane)));

        notice.ShouldBe(expected);
    }

    // ── Commands one run has running never hold more than one tier row between them ─

    [Fact]
    public async Task Two_overlapping_commands_from_one_run_run_one_after_the_other()
    {
        // Each command gets a cgroup leaf of its own, beside the agent's, carrying the run's whole tier row. Two
        // commands a run starts at once would each get a full row; queued, they never hold more than one between them.
        var runner = new OverlapRunner(expectedConcurrency: 2);
        var service = new RunCommandService(null!, null!, new SingleRunnerRegistry(runner), null!, LocalDefault(), new CallerCommandLanes());
        var runId = Guid.NewGuid();

        await Task.WhenAll(service.RunAsync(AgentAsks(PostureOf(AgentAutonomyLevel.Trusted) with { RunId = runId }), CancellationToken.None), service.RunAsync(AgentAsks(PostureOf(AgentAutonomyLevel.Trusted) with { RunId = runId }), CancellationToken.None));

        runner.Started.ShouldBe(2, "fixture check: both commands ran");
        runner.MaxConcurrent.ShouldBe(1, "a run's two commands must never run at the same time");
    }

    [Fact]
    public async Task Commands_from_two_runs_and_workflow_node_commands_are_not_queued_behind_each_other()
    {
        var runner = new OverlapRunner(expectedConcurrency: 3);
        var service = new RunCommandService(null!, null!, new SingleRunnerRegistry(runner), null!, LocalDefault(), new CallerCommandLanes());

        await Task.WhenAll(
            service.RunAsync(AgentAsks(PostureOf(AgentAutonomyLevel.Trusted) with { RunId = Guid.NewGuid() }), CancellationToken.None),
            service.RunAsync(AgentAsks(PostureOf(AgentAutonomyLevel.Trusted) with { RunId = Guid.NewGuid() }), CancellationToken.None),
            service.RunAsync(new RunCommandRequest { Command = "true" }, CancellationToken.None));

        runner.MaxConcurrent.ShouldBe(3, "the queue is per calling run: other runs' commands and a workflow node's are untouched");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static RunCommandRequest RequestFor(string lane) => lane switch
    {
        "workflow-node" => new RunCommandRequest { Command = "curl", AllowNetwork = true },
        "empty-allowlist-agent" => AgentAsks(new AgentRunPosture { Autonomy = AgentAutonomyLevel.Trusted, Permissions = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist, EgressAllowHosts = [] } }),
        "quiet-agent" => AgentAsks(new AgentRunPosture { Autonomy = AgentAutonomyLevel.Unleashed, Permissions = new AgentPermissions { Network = AgentNetworkAccess.Off } }) with { AllowNetwork = false },
        "networked-agent" => AgentAsks(PostureOf(AgentAutonomyLevel.Trusted)),
        "network-off-agent" => AgentAsks(new AgentRunPosture { Autonomy = AgentAutonomyLevel.Unleashed, Permissions = new AgentPermissions { Network = AgentNetworkAccess.Off } }),
        "allowlisted-agent" => AgentAsks(new AgentRunPosture { Autonomy = AgentAutonomyLevel.Trusted, Permissions = new AgentPermissions { Network = AgentNetworkAccess.On, Egress = AgentEgressPolicy.Allowlist, EgressAllowHosts = ["registry.npmjs.org"] } }),
        _ => throw new ArgumentOutOfRangeException(nameof(lane), lane, null),
    };

    /// <summary>A command an agent asked for through its tool fabric, wanting the network.</summary>
    private static RunCommandRequest AgentAsks(AgentRunPosture caller) => new() { Command = "curl", AllowNetwork = true, CallerPosture = caller };

    /// <summary>A run launched at <paramref name="tier"/> with the permissions that tier derives — what every producer stamps when no per-field override applies.</summary>
    private static AgentRunPosture PostureOf(AgentAutonomyLevel tier) => new() { Autonomy = tier, Permissions = AgentAutonomyPolicy.Derive(tier) };

    private static AgentDefaultRunnerSetting LocalDefault() =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [AgentDefaultRunnerSetting.ConfigurationKey] = "local" }).Build());

    private sealed class SingleRunnerRegistry(ISandboxRunner runner) : ISandboxRunnerRegistry
    {
        public IReadOnlyList<ISandboxRunner> All => [runner];
        public ISandboxRunner Resolve(string kind) => runner;
    }

    /// <summary>
    /// Records how many commands run at once. Each one waits until <paramref name="expectedConcurrency"/> are running
    /// together or a short grace elapses, so commands that CAN overlap always do, and the recorded maximum is what the
    /// service allowed rather than an accident of scheduling.
    /// </summary>
    private sealed class OverlapRunner(int expectedConcurrency) : ISandboxRunner
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;

        public int Started { get; private set; }
        public int MaxConcurrent { get; private set; }
        public string Kind => "local";

        public async Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Started++;
                _running++;
                MaxConcurrent = Math.Max(MaxConcurrent, _running);
                if (_running >= expectedConcurrency) _all.TrySetResult();
            }

            await Task.WhenAny(_all.Task, Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));

            lock (_gate) _running--;

            return new SandboxResult { Status = SandboxStatus.Success, ExitCode = 0, Stdout = "", Stderr = "" };
        }
    }

    /// <summary>Bind the deployment ceiling and host memory budget through the REAL configuration read for one projection; null is an unconfigured deployment.</summary>
    private static T WithSettings<T>(string? deployment, int? hostMemoryBudgetMb, Func<T> act)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [RuntimeSettings.MaxAutonomyKey] = deployment,
            [RuntimeSettings.AgentMemoryCeilingMbKey] = hostMemoryBudgetMb?.ToString(),
        }).Build();

        using (RuntimeSettings.Override(RuntimeSettings.Read(configuration))) return act();
    }
}
