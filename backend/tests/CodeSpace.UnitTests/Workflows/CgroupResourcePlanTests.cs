using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="CgroupResourcePlan"/> — the PURE cgroup-v2 resource-cap plan (the B4 memory/cpu/pids tier). The
/// limit-file FORMATS (memory.max bytes + memory.swap.max=0, cpu.max "quota period", pids.max count), the
/// only-positive-caps rule, the controllers the parent must enable, the race-free self-add exec prefix, and the
/// runId-derived path reconstruction (so a reaper tears down with no setup-time state AND distinct runs never collide)
/// are all verified WITHOUT a kernel — the privileged executor + its real-kernel CI E2E run the plan. Mirrors
/// <see cref="FilteredEgressPlanTests"/> for the egress plan.
/// </summary>
[Trait("Category", "Unit")]
public class CgroupResourcePlanTests
{
    private const string Root = "/sys/fs/cgroup/codespace.agents";
    private const string RunId = "Run-ABC123def456";

    [Fact]
    public void Memory_cap_writes_memory_max_in_bytes_AND_caps_swap_so_the_ceiling_is_hard()
    {
        var plan = CgroupResourcePlan.Build(Root, RunId, maxMemoryMb: 512, maxCpuPercent: 0, maxPids: 0);

        plan.ShouldNotBeNull();
        // memory.max alone is not a hard ceiling — the kernel swaps anon pages out at the limit, so the effective cap
        // is memory.max + host swap. memory.swap.max=0 closes that escape (verified-real in the slice-1 review).
        plan!.Limits.Select(l => (l.FileName, l.Value)).ShouldBe(new[]
        {
            ("memory.max", (512L * 1024 * 1024).ToString()),
            ("memory.swap.max", "0"),
        });
        plan.RequiredControllers.ShouldBe(new[] { "memory" });

        // memory.max is REQUIRED (its absence is a setup error); memory.swap.max is OPTIONAL (skipped best-effort on a
        // kernel without swap accounting, so setup doesn't fail closed — memory.max still caps).
        plan.Limits.Single(l => l.FileName == "memory.max").Optional.ShouldBeFalse();
        plan.Limits.Single(l => l.FileName == "memory.swap.max").Optional.ShouldBeTrue();
    }

    [Theory]
    [InlineData(50, "50000 100000")]    // 50% of one core
    [InlineData(100, "100000 100000")]  // exactly one core
    [InlineData(200, "200000 100000")]  // two cores
    [InlineData(25, "25000 100000")]
    public void Cpu_cap_writes_cpu_max_as_quota_against_the_100ms_period(int percent, string expected)
    {
        var plan = CgroupResourcePlan.Build(Root, RunId, maxMemoryMb: 0, maxCpuPercent: percent, maxPids: 0);

        var cpu = plan!.Limits.ShouldHaveSingleItem();
        cpu.FileName.ShouldBe("cpu.max");
        cpu.Value.ShouldBe(expected);
        CgroupResourcePlan.CpuPeriodMicros.ShouldBe(100_000);
        plan.RequiredControllers.ShouldBe(new[] { "cpu" });
    }

    [Fact]
    public void Pids_cap_writes_pids_max_as_a_count()
    {
        var plan = CgroupResourcePlan.Build(Root, RunId, maxMemoryMb: 0, maxCpuPercent: 0, maxPids: 64);

        var pids = plan!.Limits.ShouldHaveSingleItem();
        pids.FileName.ShouldBe("pids.max");
        pids.Value.ShouldBe("64");
        plan.RequiredControllers.ShouldBe(new[] { "pids" });
    }

    [Fact]
    public void Only_the_positive_caps_are_written_and_only_their_controllers_required()
    {
        // memory + pids set, cpu unset → memory.max + memory.swap.max + pids.max, cpu.max absent (kernel default stands),
        // and only the memory + pids controllers must be enabled on the parent.
        var plan = CgroupResourcePlan.Build(Root, RunId, maxMemoryMb: 256, maxCpuPercent: 0, maxPids: 32);

        plan!.Limits.Select(l => l.FileName).ShouldBe(new[] { "memory.max", "memory.swap.max", "pids.max" });
        plan.Limits.ShouldNotContain(l => l.FileName == "cpu.max");
        plan.RequiredControllers.ShouldBe(new[] { "memory", "pids" });
    }

    [Fact]
    public void No_positive_cap_returns_null_no_cgroup_needed()
    {
        // Mirrors ProcessRlimits.Wrap returning the command unchanged when no cap is requested — a null plan means the
        // wiring slots NO cgroup prefix (byte-identical to a run without it).
        CgroupResourcePlan.Build(Root, RunId, maxMemoryMb: 0, maxCpuPercent: 0, maxPids: 0).ShouldBeNull();
        CgroupResourcePlan.Build(Root, RunId, maxMemoryMb: -1, maxCpuPercent: -5, maxPids: 0).ShouldBeNull();
    }

    [Fact]
    public void Path_and_reap_handles_are_runId_derived_and_reconstructible_without_setup_state()
    {
        var plan = CgroupResourcePlan.Build(Root, RunId, maxMemoryMb: 512, maxCpuPercent: 0, maxPids: 0)!;

        // A reaper, holding only the runId + root, reconstructs the SAME cgroup path + name to tear down (no shared state).
        plan.Path.ShouldBe(CgroupResourcePlan.PathFor(Root, RunId));
        plan.Name.ShouldBe(CgroupResourcePlan.NameFor(RunId));
        plan.Path.ShouldBe($"{Root}/{plan.Name}");

        plan.ProcsFile.ShouldBe($"{plan.Path}/cgroup.procs");
        plan.KillFile.ShouldBe($"{plan.Path}/cgroup.kill");
        plan.Name.ShouldStartWith("cs-cg-");
    }

    [Fact]
    public void The_name_is_the_FULL_runId_slug_so_distinct_runs_never_collide_on_the_shared_cgroupfs()
    {
        // The cgroup DIRECTORY NAME is the contended kernel resource, so it must keep the runId's full uniqueness — a
        // truncated prefix would make two runs mkdir the same dir + share one cap (the slice-1 BLOCKER). These three
        // ids all share the leading "runabc12" prefix an 8-char truncation would have collapsed to ONE name.
        CgroupResourcePlan.NameFor("Run-ABC123def456").ShouldBe("cs-cg-runabc123def456");
        var names = new[] { "Run-ABC123def456", "run-abc1-2zzz", "RUNABC12_9999" }.Select(CgroupResourcePlan.NameFor).ToList();
        names.Distinct().Count().ShouldBe(3, "distinct runIds → distinct cgroup names (no 8-char-prefix collision)");

        CgroupResourcePlan.NameFor("abc-12").ShouldBe(CgroupResourcePlan.NameFor("abc-12"), "deterministic — a reaper reconstructs it");
        CgroupResourcePlan.NameFor("").ShouldBe("cs-cg-norunid", "a degenerate empty runId still yields a name");
    }

    [Fact]
    public void Exec_prefix_self_adds_the_child_to_the_cgroup_before_exec_race_free()
    {
        var plan = CgroupResourcePlan.Build(Root, RunId, maxMemoryMb: 512, maxCpuPercent: 0, maxPids: 0)!;

        // sh -c 'echo $$ > "<procs>" && exec "$@"' cs-cgroup  — the child writes its OWN pid then execs the real command,
        // so it is in the cgroup BEFORE the agent starts (vs an executor writing the pid after Process.Start, which races).
        plan.ExecPrefix.Count.ShouldBe(4);
        plan.ExecPrefix[0].ShouldBe("sh");
        plan.ExecPrefix[1].ShouldBe("-c");
        plan.ExecPrefix[2].ShouldBe($"echo $$ > \"{plan.ProcsFile}\" && exec \"$@\"");
        plan.ExecPrefix[3].ShouldBe("cs-cgroup", "the $0 placeholder so the appended command+args become $@");
    }

    [Fact]
    public void Path_join_tolerates_a_trailing_slash_on_the_root()
    {
        CgroupResourcePlan.PathFor("/sys/fs/cgroup/x/", RunId).ShouldBe(CgroupResourcePlan.PathFor("/sys/fs/cgroup/x", RunId));
    }
}
