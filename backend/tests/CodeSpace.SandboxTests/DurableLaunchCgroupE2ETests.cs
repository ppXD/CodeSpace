using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.SandboxTests;

/// <summary>
/// 🟢 Sandbox isolation E2E (high fidelity, Rule 12): drives the REAL durable launch (B4 wiring) — not the
/// <see cref="CgroupResourceLimit"/> executor directly — end to end. A <see cref="SandboxSpec"/> carrying a memory cap
/// is launched via <see cref="LocalProcessRunner.LaunchAsync"/> with an operator-configured cgroup root, which creates
/// the per-run cgroup leaf and runs the WHOLE supervisor chain (cgroup self-add → [netns] → [prlimit] → [bwrap] → the
/// command) inside it. Proves the durable-launch guarantees against a live kernel: (1) a process UNDER the cap runs to
/// Success, (2) one OVER it is OOM-killed → the run completes Failed, (3) the handle carries the cgroup key, and (4) the
/// cgroup is REAPED on the terminal path (no leak), and an uncapped run sets up NO cgroup (byte-identical). Needs a
/// delegated cgroup-v2 subtree, so it runs for real ONLY in the privileged sandbox-isolation CI job
/// (<c>CODESPACE_REQUIRE_CGROUP=1</c> → an un-delegatable container HARD-fails, skip ≠ pass); elsewhere it degrade-skips.
/// </summary>
[Trait("Category", "Sandbox")]
[Collection("Cgroup")]
public sealed class DurableLaunchCgroupE2ETests
{
    private readonly CgroupArenaFixture _fixture;

    public DurableLaunchCgroupE2ETests(CgroupArenaFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task The_durable_launch_caps_memory_under_the_real_cgroup_and_reaps_it()
    {
        var arena = ArenaOrSkip();
        if (arena is null) return;
        if (!arena.HasPython) { Skip("python3 (the deterministic memory hog) is not installed"); return; }

        var before = Environment.GetEnvironmentVariable(CgroupResourceLimit.CgroupRootEnvVar);
        Environment.SetEnvironmentVariable(CgroupResourceLimit.CgroupRootEnvVar, arena.Root);   // operator delegates this subtree
        try
        {
            var runner = new LocalProcessRunner();

            // UNDER the cap: a 16 MiB alloc inside a 256 MiB cap → the launched run completes Success.
            var underKey = Guid.NewGuid().ToString("N");
            var under = await DurableAllocAsync(runner, underKey, maxMemoryMb: 256, allocMib: 16);
            under.Handle.CgroupRunKey.ShouldBe(underKey, "a memory cap must launch the run inside a cgroup leaf keyed by the run, recorded on the handle for reap");
            under.Result.Status.ShouldBe(SandboxStatus.Success, $"a 16 MiB alloc under a 256 MiB cap must succeed. Stderr: {under.Result.Stderr}");
            Directory.Exists(CgroupResourcePlan.PathFor(arena.Root, underKey)).ShouldBeFalse("the run's cgroup leaf must be reaped on completion — a leak would survive here");

            // OVER the cap: a 256 MiB alloc inside a 64 MiB hard cap → the cgroup OOM killer kills it → run completes Failed.
            var overKey = Guid.NewGuid().ToString("N");
            var over = await DurableAllocAsync(runner, overKey, maxMemoryMb: 64, allocMib: 256);
            over.Result.Status.ShouldBe(SandboxStatus.Failed, $"a 256 MiB alloc under a 64 MiB hard cap must be OOM-killed by the cgroup the durable launch placed it in — the memory ceiling is the whole point. If this succeeds, the cap is not enforcing through the durable launch. Stderr: {over.Result.Stderr}");
            Directory.Exists(CgroupResourcePlan.PathFor(arena.Root, overKey)).ShouldBeFalse("the OOM'd run's cgroup leaf is reaped on the terminal path too");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CgroupResourceLimit.CgroupRootEnvVar, before);
        }
    }

    [Fact]
    public async Task The_cgroup_namespace_re_roots_the_agent_view_inside_a_real_leaf()
    {
        var arena = ArenaOrSkip();
        if (arena is null) return;

        // --unshare-cgroup-try only has TEETH when the launcher sits in a NON-root cgroup. Without bwrap the flag is
        // never applied, so this can't prove re-rooting — require the sandbox (fail-closed on the required lane).
        if (BubblewrapSandbox.Available is null)
        {
            BubblewrapSandbox.IsRequired.ShouldBeFalse("CODESPACE_REQUIRE_SANDBOX is set but no bwrap — cannot prove cgroup-namespace re-rooting without the --unshare-cgroup-try flag bwrap applies");
            return;
        }

        var before = Environment.GetEnvironmentVariable(CgroupResourceLimit.CgroupRootEnvVar);
        Environment.SetEnvironmentVariable(CgroupResourceLimit.CgroupRootEnvVar, arena.Root);
        try
        {
            // A memory cap places the whole supervisor chain (and so the bwrap'd agent) inside a real cgroup leaf
            // <arena>/cs-<key>. WITHOUT --unshare-cgroup-try the confined agent would read that real leaf path from
            // /proc/self/cgroup; WITH it bwrap re-roots the cgroup namespace at the leaf so the agent reads 0::/ and
            // cannot learn its own cgroup path. So this 0::/ assertion has TEETH — deleting the flag makes it fail.
            var runner = new LocalProcessRunner();
            var key = Guid.NewGuid().ToString("N");

            var spec = new SandboxSpec
            {
                Command = "/bin/sh",
                Args = new[] { "-c", "printf 'CGV='; grep '^0::' /proc/self/cgroup" },
                MaxMemoryMb = 128,    // a cap ⇒ a real non-root leaf, so the view would leak the leaf path WITHOUT cgroupns
                TimeoutSeconds = 30,
            };

            var handle = await runner.LaunchAsync(spec, key, CancellationToken.None);
            var lines = new List<string>();
            var result = await runner.AttachAsync(handle, (l, _) => { lines.Add(l.Trim()); return Task.CompletedTask; }, CancellationToken.None);

            handle.CgroupRunKey.ShouldBe(key, "the memory cap must place the run in a cgroup leaf — otherwise the cgroupns assertion has no teeth");
            result.Status.ShouldBe(SandboxStatus.Success, $"the cgroup-view probe runs to a clean exit. Stderr: {result.Stderr}");
            lines.ShouldContain("CGV=0::/", customMessage: "inside a real non-root cgroup leaf, --unshare-cgroup-try re-roots the agent's cgroup namespace so /proc/self/cgroup reads 0::/ — without the flag it would read the leaf path <arena>/cs-<key> and this fails");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CgroupResourceLimit.CgroupRootEnvVar, before);
        }
    }

    [Fact]
    public async Task An_uncapped_run_sets_up_no_cgroup_even_with_a_root_configured()
    {
        var arena = ArenaOrSkip();
        if (arena is null) return;

        var before = Environment.GetEnvironmentVariable(CgroupResourceLimit.CgroupRootEnvVar);
        Environment.SetEnvironmentVariable(CgroupResourceLimit.CgroupRootEnvVar, arena.Root);
        try
        {
            // Default spec (MaxMemoryMb=0, MaxCpuPercent=0): no cap requested → no cgroup leaf, byte-identical to a run
            // without the knob, EVEN with a root configured. Non-breaking by construction.
            var runner = new LocalProcessRunner();
            var key = Guid.NewGuid().ToString("N");

            var handle = await runner.LaunchAsync(new SandboxSpec { Command = "true", TimeoutSeconds = 20 }, key, CancellationToken.None);

            handle.CgroupRunKey.ShouldBeNull("no memory/cpu cap requested ⇒ no cgroup is created, even when a root is configured");
            Directory.Exists(CgroupResourcePlan.PathFor(arena.Root, key)).ShouldBeFalse("no cgroup leaf was created for an uncapped run");

            await runner.AttachAsync(handle, (_, _) => Task.CompletedTask, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CgroupResourceLimit.CgroupRootEnvVar, before);
        }
    }

    private static async Task<(SandboxHandle Handle, SandboxResult Result)> DurableAllocAsync(LocalProcessRunner runner, string runKey, int maxMemoryMb, int allocMib)
    {
        var spec = new SandboxSpec
        {
            Command = "python3",
            Args = new[] { "-c", $"b = bytearray({allocMib} * 1024 * 1024); print('ok')" },
            MaxMemoryMb = maxMemoryMb,
            TimeoutSeconds = 40,
        };

        var handle = await runner.LaunchAsync(spec, runKey, CancellationToken.None);
        var result = await runner.AttachAsync(handle, (_, _) => Task.CompletedTask, CancellationToken.None);
        return (handle, result);
    }

    /// <summary>The fixture-bootstrapped arena, or — under CODESPACE_REQUIRE_CGROUP — a HARD failure if the container can't delegate cgroups (skip ≠ pass). Loud-skips only off the required lane.</summary>
    private CgroupTestArena? ArenaOrSkip()
    {
        if (_fixture.Arena is not null) return _fixture.Arena;

        if (Environment.GetEnvironmentVariable("CODESPACE_REQUIRE_CGROUP") == "1")
            false.ShouldBeTrue($"CODESPACE_REQUIRE_CGROUP=1 but cgroup delegation is unavailable: {_fixture.Why}. The privileged sandbox lane must enforce REAL cgroup caps (fail-closed) — skip is not a pass.");

        Skip($"cgroup delegation unavailable: {_fixture.Why}");
        return null;
    }

    private static void Skip(string why) => Console.WriteLine($"[DurableLaunchCgroupE2ETests] SKIPPED (skip != pass): {why}. The privileged sandbox-isolation CI job (CODESPACE_REQUIRE_CGROUP=1) is authoritative.");
}
