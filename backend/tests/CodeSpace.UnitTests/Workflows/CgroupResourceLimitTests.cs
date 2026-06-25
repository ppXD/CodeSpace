using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Deterministic coverage of the <see cref="CgroupResourceLimit"/> executor's ORCHESTRATION (Rule 12 medium tier) —
/// driven against a temp directory standing in for cgroupfs, so every branch (controller enablement, the limit-file
/// writes, the self-add run, fail-closed setup, idempotent teardown) is exercised WITHOUT root or a real kernel. The
/// real-kernel ENFORCEMENT (a memory cap that actually OOM-kills, a pids cap that blocks a fork-bomb) is the separate
/// high-fidelity <c>CgroupResourceE2ETests</c> in the privileged sandbox lane — this pins the file-IO + process
/// orchestration that test can't isolate.
///
/// POSIX-only for the run tests (the self-add prefix is a /bin/sh wrapper); the pure file-write tests run anywhere.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CgroupResourceLimitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-cglimit-" + Guid.NewGuid().ToString("N"));

    public CgroupResourceLimitTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Setup_enables_the_required_controllers_on_the_parent_and_writes_every_limit_file()
    {
        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(_root, runId, maxMemoryMb: 256, maxCpuPercent: 50, maxPids: 64)!;

        var setup = await CgroupResourceLimit.SetupAsync(plan, CancellationToken.None);

        setup.SetupOk.ShouldBeTrue($"setup against a writable temp root succeeds; error: {setup.SetupError}");

        // Controllers enabled on the PARENT's subtree_control (so the leaf's limit files exist), as +name directives.
        File.ReadAllText(Path.Combine(_root, "cgroup.subtree_control")).ShouldBe("+memory +cpu +pids");

        // Every REQUIRED limit written into the leaf with the cgroup-v2 value format.
        File.ReadAllText(Path.Combine(plan.Path, "memory.max")).ShouldBe((256L * 1024 * 1024).ToString());
        File.ReadAllText(Path.Combine(plan.Path, "cpu.max")).ShouldBe("50000 100000");
        File.ReadAllText(Path.Combine(plan.Path, "pids.max")).ShouldBe("64");

        // memory.swap.max is OPTIONAL — its control file doesn't exist in the temp stand-in (as on a kernel without
        // swap accounting), so the executor skips it best-effort rather than failing setup closed.
        File.Exists(Path.Combine(plan.Path, "memory.swap.max")).ShouldBeFalse("an optional limit whose control file is absent is skipped, not written");

        setup.ExecPrefix.ShouldBe(plan.ExecPrefix, "the caller runs its command behind the plan's self-add prefix");
    }

    [Fact]
    public async Task An_optional_limit_is_written_when_its_control_file_already_exists()
    {
        // The real-kernel case (swap accounting ON): memory.swap.max exists, so the executor writes it. Pre-create the
        // leaf + the optional control file so File.Exists is true, mirroring a kernel where the file is present.
        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(_root, runId, maxMemoryMb: 256, maxCpuPercent: 0, maxPids: 0)!;
        Directory.CreateDirectory(plan.Path);
        File.WriteAllText(Path.Combine(plan.Path, "memory.swap.max"), "");

        (await CgroupResourceLimit.SetupAsync(plan, CancellationToken.None)).SetupOk.ShouldBeTrue();

        File.ReadAllText(Path.Combine(plan.Path, "memory.swap.max")).ShouldBe("0", "an optional limit whose control file EXISTS is written — no swap escape");
    }

    [Fact]
    public async Task Run_executes_the_command_behind_the_self_add_prefix()
    {
        if (OperatingSystem.IsWindows()) return;   // the self-add prefix is a /bin/sh wrapper

        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(_root, runId, maxMemoryMb: 64, maxCpuPercent: 0, maxPids: 0)!;

        var outcome = await CgroupResourceLimit.RunAsync(plan, "printf", new[] { "cgroup-ran-ok" }, timeoutSeconds: 20, CancellationToken.None);

        outcome.SetupOk.ShouldBeTrue($"setup error: {outcome.SetupError}");
        outcome.ExitCode.ShouldBe(0);
        // The prefix is `sh -c 'echo $$ > "<procs>" && exec "$@"' cs-cgroup <cmd>` — the `&&` means the command only
        // runs if the cgroup.procs write SUCCEEDED, so this output is proof the self-add placement ran before exec.
        outcome.Output.ShouldContain("cgroup-ran-ok", customMessage: "the self-add wrapper placed itself then exec'd the real command");
    }

    [Fact]
    public async Task Setup_fails_closed_when_the_parent_is_unwritable_and_leaves_no_partial_cgroup()
    {
        // A root whose parent directory does not exist → EnableControllers' write to <root>/cgroup.subtree_control
        // throws (directory not found) → setup must fail CLOSED, not half-create the leaf.
        var missingRoot = Path.Combine(_root, "does", "not", "exist");
        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(missingRoot, runId, maxMemoryMb: 64, maxCpuPercent: 0, maxPids: 0)!;

        var setup = await CgroupResourceLimit.SetupAsync(plan, CancellationToken.None);

        setup.SetupOk.ShouldBeFalse("an unwritable parent must fail setup, never run unconfined");
        setup.SetupError.ShouldNotBeNull();
        Directory.Exists(plan.Path).ShouldBeFalse("fail-closed: the partial cgroup leaf is cleaned up");
    }

    [Fact]
    public async Task Run_returns_the_setup_error_without_running_when_setup_fails()
    {
        var missingRoot = Path.Combine(_root, "nope");
        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(missingRoot, runId, maxMemoryMb: 64, maxCpuPercent: 0, maxPids: 0)!;

        var outcome = await CgroupResourceLimit.RunAsync(plan, "printf", new[] { "should-not-run" }, timeoutSeconds: 20, CancellationToken.None);

        outcome.SetupOk.ShouldBeFalse();
        outcome.Output.ShouldNotContain("should-not-run", customMessage: "a failed setup must NOT run the command");
    }

    [Fact]
    public async Task Teardown_is_reconstructed_from_runId_plus_root_and_is_idempotent()
    {
        var runId = Guid.NewGuid().ToString("N");
        var plan = CgroupResourcePlan.Build(_root, runId, maxMemoryMb: 64, maxCpuPercent: 0, maxPids: 0)!;
        (await CgroupResourceLimit.SetupAsync(plan, CancellationToken.None)).SetupOk.ShouldBeTrue();

        // The reaper holds only runId + root — TeardownAsync reconstructs the SAME leaf path with no setup-time state,
        // and is safe to call twice (the second is a no-op on an already-gone cgroup). Best-effort: never throws.
        await Should.NotThrowAsync(() => CgroupResourceLimit.TeardownAsync(_root, runId, CancellationToken.None));
        await Should.NotThrowAsync(() => CgroupResourceLimit.TeardownAsync(_root, runId, CancellationToken.None));

        // Teardown of a never-created run is also a safe no-op.
        await Should.NotThrowAsync(() => CgroupResourceLimit.TeardownAsync(_root, "never-created-run", CancellationToken.None));
    }
}
