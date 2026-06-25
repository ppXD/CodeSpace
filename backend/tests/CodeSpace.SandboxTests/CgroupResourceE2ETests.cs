using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): the REAL cgroup-v2 resource cap (B4) against a live kernel —
/// drives <see cref="CgroupResourceLimit"/> on real cgroupfs and proves the cap ENFORCES: a process under the memory
/// ceiling runs clean while one over it is OOM-killed (asserted via the kernel's own <c>memory.events oom_kill</c>
/// counter), a fork-heavy command hits the pids cap (<c>pids.events max</c>), a cpu cap writes <c>cpu.max</c>, and the
/// reap (cgroup.kill → rmdir, reconstructed from the runId) leaves no leak. Needs a writable cgroup-v2 delegated
/// subtree, so it runs for real ONLY in the privileged sandbox-isolation CI job — where <c>CODESPACE_REQUIRE_CGROUP=1</c>
/// makes an un-delegatable container a HARD failure (skip ≠ pass, like the bwrap lane's REQUIRE_SANDBOX). Elsewhere
/// (macOS dev) it degrade-skips LOUDLY, printing exactly why.
///
/// <para>Class-level <c>[Trait("Category", "Sandbox")]</c> — the same privileged gate as the bwrap + egress tests.</para>
/// </summary>
[Trait("Category", "Sandbox")]
public sealed class CgroupResourceE2ETests : IClassFixture<CgroupArenaFixture>
{
    private const string AllocMarker = "cs-alloc-ok";

    private readonly CgroupArenaFixture _fixture;

    public CgroupResourceE2ETests(CgroupArenaFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_process_under_the_memory_cap_runs_clean_while_one_over_it_is_OOM_killed()
    {
        var arena = ArenaOrSkip();
        if (arena is null) return;
        if (!arena.HasPython) { Skip("python3 (the deterministic memory hog) is not installed"); return; }

        // Under-cap: a 32 MiB alloc inside a 256 MiB cap (well above python3's own baseline RSS) → runs to completion.
        var underCap = await arena.RunCappedAsync(maxMemoryMb: 256, maxCpuPercent: 0, maxPids: 0,
            command: "python3", args: new[] { "-c", AllocPython(32) }, timeoutSeconds: 30);
        underCap.SetupOk.ShouldBeTrue($"cgroup setup must succeed; error: {underCap.SetupError}");
        underCap.ExitCode.ShouldBe(0, $"a 32 MiB alloc under a 256 MiB cap must succeed. Output: {underCap.Output}");
        underCap.Output.ShouldContain(AllocMarker, customMessage: "the under-cap process completed its allocation");

        // Over-cap: a 256 MiB alloc inside a 64 MiB hard cap (swap capped at 0) → the cgroup OOM killer fires. Read
        // memory.events `oom_kill` — the kernel's authoritative counter — BEFORE teardown, via the Setup/run/read split.
        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(arena.Root, runId, maxMemoryMb: 64, maxCpuPercent: 0, maxPids: 0)!;
        try
        {
            (await CgroupResourceLimit.SetupAsync(plan, CancellationToken.None)).SetupOk.ShouldBeTrue("over-cap setup must succeed");

            var output = await CgroupTestArena.RunViaPrefixAsync(plan.ExecPrefix, "python3", new[] { "-c", AllocPython(256) }, timeoutSeconds: 30);

            var oomKills = arena.ReadMemoryEventsOomKill(plan.Path);
            oomKills.ShouldNotBeNull("memory.events must exist on a memory-capped cgroup");
            oomKills!.Value.ShouldBeGreaterThan(0, $"the cgroup OOM killer must fire for a 256 MiB alloc under a 64 MiB hard cap — the memory ceiling is the whole point. Output: {output}");
            output.ShouldNotContain(AllocMarker, customMessage: "the over-cap process must be killed BEFORE completing the allocation");
        }
        finally
        {
            await CgroupResourceLimit.TeardownAsync(arena.Root, runId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_fork_heavy_command_hits_the_pids_cap()
    {
        var arena = ArenaOrSkip();
        if (arena is null) return;

        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(arena.Root, runId, maxMemoryMb: 0, maxCpuPercent: 0, maxPids: 3)!;

        try
        {
            (await CgroupResourceLimit.SetupAsync(plan, CancellationToken.None)).SetupOk.ShouldBeTrue("pids cgroup setup must succeed");

            // 12 concurrent /bin/sleep forks behind the self-add prefix — far past the pids cap of 3, so the kernel
            // denies the excess forks and bumps pids.events `max`.
            await CgroupTestArena.RunViaPrefixAsync(plan.ExecPrefix,
                "sh", new[] { "-c", "for i in 1 2 3 4 5 6 7 8 9 10 11 12; do sleep 2 & done; wait" }, timeoutSeconds: 20);

            var maxEvents = arena.ReadPidsEventsMax(plan.Path);
            maxEvents.ShouldNotBeNull("pids.events must exist on a pids-capped cgroup");
            maxEvents!.Value.ShouldBeGreaterThan(0, "the pids cap denied at least one fork — the fork-bomb cap is enforcing");
        }
        finally
        {
            await CgroupResourceLimit.TeardownAsync(arena.Root, runId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task A_cpu_cap_enables_the_cpu_controller_and_writes_cpu_max_on_the_real_cgroup()
    {
        var arena = ArenaOrSkip();
        if (arena is null) return;

        // Exercises the +cpu controller-enablement + the cpu.max quota write end to end on the real kernel (a timing
        // throttle assertion would be flaky; the written cpu.max IS the enforcement knob — its presence proves +cpu was
        // enabled on the parent and the quota took). Caught the slice-2 blocker where the arena delegated only mem+pids.
        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(arena.Root, runId, maxMemoryMb: 0, maxCpuPercent: 50, maxPids: 0)!;

        try
        {
            (await CgroupResourceLimit.SetupAsync(plan, CancellationToken.None)).SetupOk.ShouldBeTrue("a cpu cap must set up — the +cpu controller is enabled + cpu.max written");

            arena.ReadCpuMax(plan.Path).ShouldBe("50000 100000", "cpu.max carries the 50%-of-one-core quota against the 100ms period, on the real cgroup");
        }
        finally
        {
            await CgroupResourceLimit.TeardownAsync(arena.Root, runId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_setup_teardown_split_reaps_the_cgroup_and_is_reconstructable_from_runId()
    {
        var arena = ArenaOrSkip();
        if (arena is null) return;

        // The contract the durable launch (slice 3) relies on: SetupAsync builds the capped cgroup + returns the
        // ExecPrefix, and TeardownAsync — reconstructed PURELY from runId + root — reaps it at reap (possibly on a
        // different worker after a crash). Proven leak-free: a re-setup with the SAME runId succeeds only if teardown
        // actually rmdir'd the leaf.
        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(arena.Root, runId, maxMemoryMb: 64, maxCpuPercent: 0, maxPids: 0)!;

        (await CgroupResourceLimit.SetupAsync(plan, CancellationToken.None)).SetupOk.ShouldBeTrue();
        Directory.Exists(plan.Path).ShouldBeTrue("the leaf cgroup exists after setup");

        await CgroupResourceLimit.TeardownAsync(arena.Root, runId, CancellationToken.None);   // reconstructed from runId alone
        Directory.Exists(plan.Path).ShouldBeFalse("teardown rmdir'd the leaf cgroup — no leak");

        var resetup = await CgroupResourceLimit.SetupAsync(plan, CancellationToken.None);
        resetup.SetupOk.ShouldBeTrue("teardown freed the runId-derived leaf so a re-setup succeeds — the reap/crash-resume contract");
        await CgroupResourceLimit.TeardownAsync(arena.Root, runId, CancellationToken.None);
    }

    /// <summary>A python one-liner allocating <paramref name="mib"/> MiB of touched (real RSS) memory then printing the marker — absent iff the process was killed mid-allocation.</summary>
    private static string AllocPython(int mib) => $"b = bytearray({mib} * 1024 * 1024); print('{AllocMarker}')";

    /// <summary>The fixture-bootstrapped arena (created ONCE for the class), or — when <c>CODESPACE_REQUIRE_CGROUP=1</c> (the privileged lane) — FAIL hard if the container can't delegate cgroups (skip ≠ pass). Returns null + loud-skips only off the required lane (macOS dev).</summary>
    private CgroupTestArena? ArenaOrSkip()
    {
        if (_fixture.Arena is not null) return _fixture.Arena;

        if (Environment.GetEnvironmentVariable("CODESPACE_REQUIRE_CGROUP") == "1")
            false.ShouldBeTrue($"CODESPACE_REQUIRE_CGROUP=1 but cgroup delegation is unavailable: {_fixture.Why}. The privileged sandbox lane must enforce REAL cgroup caps (fail-closed, like REQUIRE_SANDBOX) — skip is not a pass.");

        Skip($"cgroup delegation unavailable: {_fixture.Why}");
        return null;
    }

    private static void Skip(string why) => Console.WriteLine($"[CgroupResourceE2ETests] SKIPPED (skip != pass): {why}. The privileged sandbox-isolation CI job (CODESPACE_REQUIRE_CGROUP=1) is authoritative.");
}
