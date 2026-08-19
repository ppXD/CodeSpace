using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the autonomy-tier → resource-ceiling table AND that the spec the executor hands the runner actually carries
/// it. Two separate claims, because for a long time the first was true and the second was not: the cgroup machinery
/// existed and was tested, but no production path ever wrote <see cref="SandboxSpec.MaxMemoryMb"/> /
/// <see cref="SandboxSpec.MaxCpuPercent"/>, so every run launched uncapped and one runaway agent took the worker with it.
/// </summary>
[Trait("Category", "Unit")]
public class AgentResourceCeilingTests
{
    private static AgentTask Task(AgentAutonomyLevel level) => new()
    {
        Goal = "Fix the failing billing tests",
        Harness = ClaudeCodeHarness.HarnessKind,
        WorkspaceDirectory = "/tmp/ws",
        Autonomy = level,
        Permissions = AgentAutonomyPolicy.Derive(level),
        TimeoutSeconds = 900,
    };

    /// <summary>The spec as the executor finally hands it to the runner — the harness invocation after the executor's own post-processing.</summary>
    private static SandboxSpec BuiltSpec(AgentAutonomyLevel level, int? hostMemoryBudgetMb = null)
    {
        var task = Task(level);
        var built = AgentRunExecutor.ApplyEgressPolicy(new ClaudeCodeHarness().BuildInvocation(task), task.Permissions, modelBaseUrl: null, modelProvider: null, workspace: null);

        return AgentRunExecutor.ApplyResourceCeilings(built, task.Autonomy, hostMemoryBudgetMb);
    }

    [Theory]
    [InlineData(AgentAutonomyLevel.Confined, 1024, 100)]
    [InlineData(AgentAutonomyLevel.Standard, 4096, 400)]
    [InlineData(AgentAutonomyLevel.Trusted, 6144, 400)]
    [InlineData(AgentAutonomyLevel.Unleashed, 6144, 400)]
    public void The_spec_the_executor_hands_the_runner_carries_the_tier_s_ceilings(AgentAutonomyLevel level, int memoryMb, int cpuPercent)
    {
        var spec = BuiltSpec(level);

        spec.MaxMemoryMb.ShouldBe(memoryMb, customMessage: "the tier's committed memory ceiling must reach the runner — a 0 here means the run launches uncapped and a runaway agent OOMs the worker");
        spec.MaxCpuPercent.ShouldBe(cpuPercent, customMessage: "the tier's committed cpu quota must reach the runner — a 0 here means the run can starve the worker's own heartbeats");
    }

    [Fact]
    public void The_plan_the_durable_launch_builds_from_that_spec_carries_the_ceilings_as_cgroup_limit_values()
    {
        // The last link: the durable launch feeds the spec's two ceilings straight into CgroupResourcePlan.Build, so
        // this asserts the exact kernel-file values a Standard run ends up capped by. Pure plan — no cgroupfs, so it
        // runs on macOS; the KERNEL actually honouring these files is the privileged CodeSpace.SandboxTests job.
        var spec = BuiltSpec(AgentAutonomyLevel.Standard);

        var plan = CgroupResourcePlan.Build("/sys/fs/cgroup/codespace", "run-1", spec.MaxMemoryMb, spec.MaxCpuPercent, maxPids: 0);

        plan.ShouldNotBeNull(customMessage: "a positive ceiling must produce a plan — a null plan means the launch slots no cgroup prefix and the run is uncapped");
        plan.Limits.Single(l => l.FileName == "memory.max").Value.ShouldBe((4096L * 1024 * 1024).ToString());
        plan.Limits.Single(l => l.FileName == "cpu.max").Value.ShouldBe("400000 100000", customMessage: "400% of one core against the kernel's 100ms period");
    }

    [Fact]
    public void Every_tier_is_pinned_so_a_new_one_cannot_ship_uncapped()
    {
        // Adding a tier means adding its row to the Theory above AND bumping this count. The fall-through arm is
        // Confined's row rather than "unlimited", so a missed tier is over-restricted (a visible, recoverable failure)
        // instead of silently uncapped (the failure this whole lane exists to end).
        Enum.GetValues<AgentAutonomyLevel>().Length.ShouldBe(4);

        var unknown = AgentAutonomyPolicy.Ceilings((AgentAutonomyLevel)9_999, hostMemoryBudgetMb: null);

        unknown.ShouldBe(AgentAutonomyPolicy.Ceilings(AgentAutonomyLevel.Confined, hostMemoryBudgetMb: null),
            customMessage: "an unrecognised tier must fall back to the MOST restrictive row, never to unlimited");
    }

    [Theory]
    // Lower than the tier's committed row → the operator's host budget wins (the smaller-host case it exists for).
    [InlineData(1536, 1536)]
    // At or above it → the committed row stands: the budget NARROWS only, so it can never grant an agent more.
    [InlineData(4096, 4096)]
    [InlineData(65536, 4096)]
    // Zero / negative are ignored, not read as "unlimited" — the knob is a value, never a switch that turns the
    // ceilings off (the owner's standing rule against on/off env toggles, issue #1235).
    [InlineData(0, 4096)]
    [InlineData(-1, 4096)]
    public void The_operator_s_host_memory_budget_can_only_narrow_a_tier_s_committed_ceiling(int hostMemoryBudgetMb, int expectedMemoryMb)
    {
        var spec = BuiltSpec(AgentAutonomyLevel.Standard, hostMemoryBudgetMb);

        spec.MaxMemoryMb.ShouldBe(expectedMemoryMb);
        spec.MaxCpuPercent.ShouldBe(400, customMessage: "the budget is memory-only — a cpu overrun throttles rather than kills, so there is no cpu twin to narrow");
    }

    [Theory]
    // The operator's configuration key. Renaming it silently restores the committed ceiling for every deployment that
    // narrowed it, so the literal is pinned here (Rule 8) rather than only living at the read site.
    [InlineData("1536", 1536)]
    [InlineData("0", null)]
    [InlineData("-4", null)]
    [InlineData("not-a-number", null)]
    [InlineData(null, null)]
    public void The_host_memory_budget_is_read_from_its_pinned_configuration_key(string? configured, int? expected)
    {
        var values = configured is null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["Sandbox:AgentMemoryCeilingMb"] = configured };

        var settings = RuntimeSettings.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        settings.AgentMemoryCeilingMb.ShouldBe(expected, customMessage: "a blank / zero / unparseable value must land on the committed default, never on 'no limit'");
    }
}
