using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Settings;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins that a run the cgroup memory ceiling killed is reported AS a resource kill rather than as an agent fault.
/// The kernel's own <c>memory.events</c> <c>oom_kill</c> counter for the run's leaf is the evidence — not an inference
/// from the signal number, which cannot separate a cgroup OOM from a host teardown or an operator's <c>kill -9</c>.
///
/// <para>Fidelity: HIGH for the classification (a REAL durable launch under the real /bin/sh spool supervisor, the real
/// attach path, the real exit marker), MEDIUM for the cgroup itself — the leaf's <c>memory.events</c> is staged as a
/// file under a temp root because macOS has no cgroupfs. What the KERNEL does when <c>memory.max</c> is exceeded is
/// covered by the privileged <c>CodeSpace.SandboxTests</c> job, not here. POSIX-only (the supervisor is /bin/sh), so
/// each test skips on Windows (Rule 12.1); the staged root is GUID-keyed and cleaned up (Rule 12.2/12.3).</para>
/// </summary>
[Trait("Category", "Unit")]
[Collection("LocalProcessIdleWatchdog")]
public sealed class SandboxResourceKillTests : IDisposable
{
    private readonly LocalProcessRunner _runner = new();
    private readonly List<string> _directories = new();

    /// <summary>Stage a cgroup leaf for <paramref name="runKey"/> under a fresh fake root, with the kernel counter file the classifier reads.</summary>
    private string StageLeaf(string runKey, string memoryEvents)
    {
        var root = Path.Combine(Path.GetTempPath(), "cs-cgtest-" + Guid.NewGuid().ToString("N"));
        var leaf = CgroupResourcePlan.PathFor(root, runKey);

        Directory.CreateDirectory(leaf);
        File.WriteAllText(Path.Combine(leaf, "memory.events"), memoryEvents);
        _directories.Add(root);

        return root;
    }

    private async Task<SandboxResult> LaunchAndAttachAsync(string runKey, int exitCode, string root)
    {
        using var settings = RuntimeSettings.Override(s => s with { AgentCgroupRoot = root });

        var launched = await _runner.LaunchAsync(ContractSpecs.ExitWith(exitCode), runKey, CancellationToken.None);
        _directories.Add(launched.SpoolDirectory);

        // The staged leaf stands in for what a cgroup-v2 host's LaunchAsync would have recorded on the handle.
        return await _runner.AttachAsync(launched with { CgroupRunKey = runKey }, (_, _) => Task.CompletedTask, CancellationToken.None);
    }

    /// <summary>Launch, observe to its natural exit, and hand back the handle — so a follow-up attach observes a run whose process is genuinely dead.</summary>
    private async Task<SandboxHandle> LaunchAndCompleteAsync(string runKey, int exitCode, string root)
    {
        using var settings = RuntimeSettings.Override(s => s with { AgentCgroupRoot = root });

        var launched = await _runner.LaunchAsync(ContractSpecs.ExitWith(exitCode), runKey, CancellationToken.None);
        _directories.Add(launched.SpoolDirectory);

        await _runner.AttachAsync(launched, (_, _) => Task.CompletedTask, CancellationToken.None);

        return launched;
    }

    [Fact]
    public async Task An_exit_the_cgroup_oom_killer_caused_is_reported_as_a_resource_kill_not_a_plain_failure()
    {
        if (OperatingSystem.IsWindows()) return;

        var runKey = Guid.NewGuid().ToString("N");
        var root = StageLeaf(runKey, "low 0\nhigh 0\nmax 3\noom 1\noom_kill 1\n");

        var result = await LaunchAndAttachAsync(runKey, exitCode: 137, root);

        result.Status.ShouldBe(SandboxStatus.ResourceExhausted,
            customMessage: "the kernel OOM-killed this run's subtree at its memory ceiling — reporting a plain Failed sends the retry path after a run that will die identically");
        result.ExitCode.ShouldBe(137, customMessage: "the real exit code is kept — the operator still sees 128+SIGKILL alongside the resource classification");
    }

    [Fact]
    public async Task A_plain_non_zero_exit_with_no_oom_kill_recorded_stays_a_plain_failure()
    {
        if (OperatingSystem.IsWindows()) return;

        // The other side of the same read: a capped run that simply failed must NOT be laundered into a resource kill,
        // or every genuine agent failure on a cgroup host becomes non-retryable.
        var runKey = Guid.NewGuid().ToString("N");
        var root = StageLeaf(runKey, "low 0\nhigh 0\nmax 0\noom 0\noom_kill 0\n");

        var result = await LaunchAndAttachAsync(runKey, exitCode: 1, root);

        result.Status.ShouldBe(SandboxStatus.Failed, customMessage: "oom_kill 0 means the ceiling was never hit — this is the agent's own failure");
    }

    [Fact]
    public async Task A_successful_exit_is_never_reclassified_even_when_a_descendant_was_oom_killed()
    {
        if (OperatingSystem.IsWindows()) return;

        // A child the agent itself over-allocated can be OOM-killed inside the ceiling while the agent recovers and
        // finishes cleanly. The run succeeded; only a non-zero exit is a candidate for reclassification.
        var runKey = Guid.NewGuid().ToString("N");
        var root = StageLeaf(runKey, "oom 1\noom_kill 1\n");

        var result = await LaunchAndAttachAsync(runKey, exitCode: 0, root);

        result.Status.ShouldBe(SandboxStatus.Success, customMessage: "the agent exited 0 — a reaped descendant it survived must not fail the run");
    }

    [Fact]
    public async Task A_run_that_vanished_with_no_exit_marker_is_a_resource_kill_when_its_cgroup_recorded_one()
    {
        if (OperatingSystem.IsWindows()) return;

        // The other terminal a capped run can land on: the OOM killer took the /bin/sh supervisor too, so nothing wrote
        // an exit marker and the observer finds only a dead pid. Without the same cgroup read, that run reports the
        // generic "vanished" failure and buys a retry at the identical ceiling.
        var runKey = Guid.NewGuid().ToString("N");
        var emptyRoot = StageLeaf(Guid.NewGuid().ToString("N"), "oom_kill 0\n");   // a root with no leaf for THIS run

        var handle = await LaunchAndCompleteAsync(runKey, exitCode: 1, emptyRoot);

        File.Delete(Path.Combine(handle.SpoolDirectory, "exit"));
        var root = StageLeaf(runKey, "oom 1\noom_kill 1\n");

        using var settings = RuntimeSettings.Override(s => s with { AgentCgroupRoot = root });
        var result = await _runner.AttachAsync(handle with { CgroupRunKey = runKey }, (_, _) => Task.CompletedTask, CancellationToken.None);

        result.Status.ShouldBe(SandboxStatus.ResourceExhausted,
            customMessage: "a marker-less kill inside a cgroup that recorded an oom_kill is still the ceiling's kill — the counter is the evidence, the exit code was never written");
        result.ExitCode.ShouldBe(-1, customMessage: "no marker means no observed code — the -1 sentinel is kept rather than invented");
    }

    [Fact]
    public void The_executor_maps_a_resource_kill_to_a_failure_whose_reason_names_the_ceiling()
    {
        var sandbox = new SandboxResult { Status = SandboxStatus.ResourceExhausted, ExitCode = 137, Stdout = "", Stderr = "" };

        var result = AgentRunExecutor.MapSandboxResult(sandbox, folder: null!, new AgentRunFacts());

        result.ExitReason.ShouldBe(AgentRunExecutor.ResourceExhaustedExitReason,
            customMessage: "the producer stamps this literal and the agent.run node's retry verdict keys on it — a rename on one side silently restores the retry loop");
        result.Status.ShouldBe(AgentRunStatus.Failed);
        result.Error.ShouldNotBeNull().ShouldContain("resource", Case.Insensitive,
            customMessage: "the operator's first read of the run must say a ceiling killed it, not repeat the agent's last chatty message");
    }

    public void Dispose()
    {
        foreach (var directory in _directories)
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
    }
}
