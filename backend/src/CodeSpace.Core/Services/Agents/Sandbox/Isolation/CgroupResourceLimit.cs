using System.Diagnostics;
using System.Text;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// The privileged executor of a <see cref="CgroupResourcePlan"/> (B4 enforcement) — enables the required controllers on
/// the plan's parent, creates the run's cgroup-v2 leaf, writes the limit files, runs a command behind the plan's
/// self-add prefix (so the agent + every descendant are capped), and reaps the cgroup. Needs a writable cgroup-v2
/// delegated subtree, so it runs for real only in the privileged sandbox-isolation CI job; <see cref="IsSupported"/>
/// gates it everywhere else and a setup failure fails CLOSED. Teardown is BEST-EFFORT, reconstructed PURELY from the
/// runId + root, and ALWAYS runs (even on a setup failure mid-way), so a failed run never leaks a cgroup or its cap.
/// Mirrors <see cref="FilteredEgressNetns"/> for egress — the same Setup/Teardown/Run shape, but cgroupfs file IO
/// (mkdir + write + rmdir) instead of <c>ip</c>/<c>nft</c> processes.
/// </summary>
public static class CgroupResourceLimit
{
    private const int DrainPollMs = 50;
    private const int DrainTimeoutMs = 5000;

    private static readonly Lazy<bool> _supported = new(ProbeSupported);

    /// <summary>True on Linux with a cgroup-v2 unified mount (<c>/sys/fs/cgroup/cgroup.controllers</c> present). The actual write/delegation privilege is exercised at setup, which fails CLOSED.</summary>
    public static bool IsSupported => _supported.Value;

    /// <summary>Operator escape-hatch (Rule 8): the DELEGATED cgroup-v2 root the durable launch creates per-run leaves under. UNSET ⇒ no resource cap is applied (byte-identical to a run without one) — the operator opts in by delegating a subtree + setting this. Pinned by a test.</summary>
    public const string CgroupRootEnvVar = "CODESPACE_AGENT_CGROUP_ROOT";

    /// <summary>The configured delegated cgroup root (<see cref="CgroupRootEnvVar"/>), or null when the operator has not delegated a subtree — in which case the durable launch applies no cgroup cap.</summary>
    public static string? CgroupRoot
    {
        get
        {
            var v = Environment.GetEnvironmentVariable(CgroupRootEnvVar);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
    }

    /// <summary>The outcome of SETTING UP the cgroup (without running anything in it) — for the durable launch, which runs its detached process behind <see cref="ExecPrefix"/> and tears down separately at reap. On failure the partial cgroup is already cleaned up (fail-closed).</summary>
    public sealed record SetupResult
    {
        public required bool SetupOk { get; init; }

        /// <summary>The self-add prefix a caller prepends so its command places itself into the cgroup before exec. Empty when setup failed.</summary>
        public IReadOnlyList<string> ExecPrefix { get; init; } = Array.Empty<string>();

        public string? SetupError { get; init; }
    }

    /// <summary>The outcome of running a command inside the cgroup: the command's exit code + combined output, plus whether the cgroup setup itself succeeded.</summary>
    public sealed record Outcome
    {
        public required bool SetupOk { get; init; }
        public int ExitCode { get; init; }
        public string Output { get; init; } = "";
        public string? SetupError { get; init; }
    }

    /// <summary>
    /// Create the run's cgroup from <paramref name="plan"/> — enable its controllers on the parent's
    /// <c>cgroup.subtree_control</c>, <c>mkdir</c> the leaf, write the limit files — WITHOUT running anything in it. The
    /// durable launch then runs its detached process behind <see cref="SetupResult.ExecPrefix"/> and calls
    /// <see cref="TeardownAsync"/> at reap. Any failure is fail-closed: the partial cgroup is reaped immediately and
    /// SetupOk=false is returned.
    /// </summary>
    public static async Task<SetupResult> SetupAsync(CgroupResourcePlan plan, CancellationToken cancellationToken)
    {
        try
        {
            EnableControllers(plan);

            Directory.CreateDirectory(plan.Path);

            foreach (var limit in plan.Limits)
                await WriteLimitAsync(plan.Path, limit, cancellationToken).ConfigureAwait(false);

            return new SetupResult { SetupOk = true, ExecPrefix = plan.ExecPrefix };
        }
        catch (Exception ex)
        {
            await TeardownPathAsync(plan.Path).ConfigureAwait(false);   // fail-closed: never leave a half-built cgroup
            return new SetupResult { SetupOk = false, SetupError = $"cgroup setup failed at {plan.Path}: {ex.Message}" };
        }
    }

    /// <summary>
    /// Tear down the run's cgroup — reconstructed PURELY from <paramref name="cgroupRoot"/> + <paramref name="runId"/>
    /// (the leaf path is runId-derived), so it works even when called by a DIFFERENT worker after a crash/resume, or by
    /// the reaper, with no setup-time state. Atomic whole-subtree kill via <c>cgroup.kill</c> (else SIGKILL each
    /// <c>cgroup.procs</c> member), drains, then <c>rmdir</c>. Best-effort + idempotent: a gone cgroup is a no-op.
    /// </summary>
    public static Task TeardownAsync(string cgroupRoot, string runId, CancellationToken cancellationToken) =>
        TeardownPathAsync(CgroupResourcePlan.PathFor(cgroupRoot, runId));

    /// <summary>
    /// Run <paramref name="command"/> inside a fresh cgroup capped by <paramref name="plan"/>. Sets up, runs behind the
    /// self-add prefix, and ALWAYS tears down — the SYNCHRONOUS path the B4 CI E2E drives. The durable launch uses
    /// <see cref="SetupAsync"/> + <see cref="TeardownAsync"/> directly instead.
    /// </summary>
    public static async Task<Outcome> RunAsync(CgroupResourcePlan plan, string command, IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var setup = await SetupAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!setup.SetupOk) return new Outcome { SetupOk = false, SetupError = setup.SetupError };

        try
        {
            var argv = setup.ExecPrefix.Concat(new[] { command }).Concat(args).ToList();
            var (exit, output) = await RunHostAsync(argv, timeoutSeconds, cancellationToken).ConfigureAwait(false);

            return new Outcome { SetupOk = true, ExitCode = exit, Output = output };
        }
        finally
        {
            await TeardownPathAsync(plan.Path).ConfigureAwait(false);
        }
    }

    /// <summary>Write one limit into the leaf. A REQUIRED limit's failure propagates (SetupAsync fails closed); an OPTIONAL limit (e.g. <c>memory.swap.max</c> on a kernel without swap accounting) is best-effort — its control file being absent is skipped, so <c>memory.max</c> still caps rather than the whole setup failing.</summary>
    private static async Task WriteLimitAsync(string leaf, CgroupLimit limit, CancellationToken cancellationToken)
    {
        var file = Path.Combine(leaf, limit.FileName);

        if (limit.Optional && !File.Exists(file)) return;

        await File.WriteAllTextAsync(file, limit.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Enable the plan's controllers on the parent's <c>cgroup.subtree_control</c> (as <c>+memory +pids …</c>) so the leaf's limit files exist — a leaf <c>memory.max</c> write ENOENTs otherwise. No-op when the plan needs no controllers.</summary>
    private static void EnableControllers(CgroupResourcePlan plan)
    {
        if (plan.RequiredControllers.Count == 0) return;

        var parent = Directory.GetParent(plan.Path)?.FullName ?? throw new InvalidOperationException($"cgroup path has no parent: {plan.Path}");
        var directive = string.Join(" ", plan.RequiredControllers.Select(c => "+" + c));

        File.WriteAllText(Path.Combine(parent, "cgroup.subtree_control"), directive);
    }

    /// <summary>Kill + remove the cgroup at <paramref name="path"/>. Best-effort throughout — teardown must never throw.</summary>
    private static async Task TeardownPathAsync(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            KillMembers(path);
            await WaitForDrainAsync(path).ConfigureAwait(false);

            try { Directory.Delete(path); } catch { /* best-effort rmdir — a still-populated cgroup is retried next reap */ }
        }
        catch { /* best-effort — never let teardown throw */ }
    }

    /// <summary>Atomically kill the whole subtree via <c>cgroup.kill</c> (cgroup-v2 ≥ 5.14); fall back to SIGKILLing each <c>cgroup.procs</c> member on an older kernel.</summary>
    private static void KillMembers(string path)
    {
        var killFile = Path.Combine(path, "cgroup.kill");

        if (File.Exists(killFile))
        {
            try { File.WriteAllText(killFile, "1"); return; } catch { /* fall through to the per-pid path */ }
        }

        foreach (var pid in ReadProcs(path))
            try { Process.GetProcessById(pid).Kill(entireProcessTree: true); } catch { /* already gone / not ours */ }
    }

    /// <summary>Poll <c>cgroup.procs</c> until the cgroup is empty (so <c>rmdir</c> succeeds), bounded so a stuck member never hangs teardown.</summary>
    private static async Task WaitForDrainAsync(string path)
    {
        var deadline = Environment.TickCount64 + DrainTimeoutMs;

        while (ReadProcs(path).Count > 0 && Environment.TickCount64 < deadline)
            await Task.Delay(DrainPollMs).ConfigureAwait(false);
    }

    /// <summary>The live PIDs in the cgroup (its <c>cgroup.procs</c>), empty when the file is gone/unreadable.</summary>
    private static IReadOnlyList<int> ReadProcs(string path)
    {
        try
        {
            return File.ReadAllLines(Path.Combine(path, "cgroup.procs"))
                .Select(l => int.TryParse(l.Trim(), out var pid) ? pid : -1)
                .Where(pid => pid > 0)
                .ToList();
        }
        catch { return Array.Empty<int>(); }
    }

    private static bool ProbeSupported()
    {
        if (!OperatingSystem.IsLinux()) return false;

        try { return File.Exists("/sys/fs/cgroup/cgroup.controllers"); }
        catch { return false; }
    }

    private static async Task<(int Exit, string Output)> RunHostAsync(IReadOnlyList<string> argv, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo { FileName = argv[0], RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ } return (124, output.ToString() + "\n[timed out]"); }

        return (process.ExitCode, output.ToString());
    }
}
