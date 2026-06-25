using System.Diagnostics;
using CodeSpace.Core.Services.Agents.Sandbox.Isolation;

namespace CodeSpace.SandboxTests;

/// <summary>
/// Bootstraps a writable, controller-enabled cgroup-v2 subtree the <see cref="CgroupResourceE2ETests"/> create their
/// capped leaves under — or reports exactly WHY it can't (so the E2E skips loudly or, when REQUIRE_CGROUP is set, fails
/// hard; never silently passes). It enables the memory + cpu + pids controllers on the test process's own cgroup so
/// child cgroups can carry those limits, then hands out a GUID-unique arena directory (Rule 12.2). cgroup-v2's "no
/// internal processes" rule means a cgroup with processes can't directly enable controllers for its children UNLESS it
/// is the cgroup-namespace root (the common <c>--privileged</c> + private-cgroupns case); the fallback moves the
/// cgroup's processes into a sibling leaf first. <see cref="Dispose"/> kill-drains + reaps every capped leaf + the
/// arena (Rule 12.3); the leaf-ify <c>_cs-host</c> is left in place on purpose — it holds the LIVE container processes
/// (incl. this test host), so reaping it would kill the run, and the container is ephemeral anyway.
/// </summary>
public sealed class CgroupTestArena : IDisposable
{
    private const string CgroupMount = "/sys/fs/cgroup";
    private const int DrainTimeoutMs = 5000;

    private static readonly string[] Controllers = { "memory", "cpu", "pids" };
    private static string EnableDirective => string.Join(" ", Controllers.Select(c => "+" + c));

    /// <summary>The controller-enabled directory the executor creates its capped leaves under.</summary>
    public string Root { get; }

    /// <summary>Whether <c>python3</c> (the deterministic memory hog the OOM test drives) is on PATH.</summary>
    public bool HasPython { get; }

    private CgroupTestArena(string root, bool hasPython)
    {
        Root = root;
        HasPython = hasPython;
    }

    /// <summary>Try to bootstrap an arena; returns null + sets <paramref name="why"/> when the environment can't delegate cgroups (not Linux, no cgroup-v2, missing controllers, or an un-writable hierarchy).</summary>
    public static CgroupTestArena? TryCreate(out string why)
    {
        why = "";

        if (!CgroupResourceLimit.IsSupported) { why = "not Linux, or no cgroup-v2 unified mount at /sys/fs/cgroup"; return null; }

        var cur = CurrentCgroupDir();
        if (cur is null) { why = "could not resolve the current cgroup from /proc/self/cgroup"; return null; }

        if (!ControllersAvailable(cur, out var missing)) { why = $"the current cgroup is missing controllers [{missing}] in its cgroup.controllers — the host hierarchy hasn't delegated them"; return null; }

        if (!TryEnableControllers(cur, out var enableErr)) { why = $"could not enable [{string.Join(",", Controllers)}] on {cur}/cgroup.subtree_control ({enableErr}) — the container can't delegate cgroups"; return null; }

        var root = Path.Combine(cur, "cs-arena-" + Guid.NewGuid().ToString("N"));
        try { Directory.CreateDirectory(root); }
        catch (Exception ex) { why = $"could not create the arena cgroup {root}: {ex.Message}"; return null; }

        if (!ControllersAvailable(root, out var rootMissing))
        {
            try { Directory.Delete(root); } catch { /* best-effort */ }
            why = $"the arena cgroup {root} did not inherit controllers [{rootMissing}] — enablement did not propagate";
            return null;
        }

        return new CgroupTestArena(root, ProbePython());
    }

    /// <summary>Run <paramref name="command"/> in a fresh capped leaf under <see cref="Root"/> via the real executor (Setup → run-behind-self-add → Teardown).</summary>
    public Task<CgroupResourceLimit.Outcome> RunCappedAsync(int maxMemoryMb, int maxCpuPercent, int maxPids, string command, IReadOnlyList<string> args, int timeoutSeconds)
    {
        var plan = CgroupResourcePlan.Build(Root, Guid.NewGuid().ToString("N"), maxMemoryMb, maxCpuPercent, maxPids)!;

        return CgroupResourceLimit.RunAsync(plan, command, args, timeoutSeconds, CancellationToken.None);
    }

    /// <summary>Run a command behind an already-set-up cgroup's self-add <paramref name="execPrefix"/> (the durable-launch shape) and return its combined output.</summary>
    public static async Task<string> RunViaPrefixAsync(IReadOnlyList<string> execPrefix, string command, IReadOnlyList<string> args, int timeoutSeconds)
    {
        var argv = execPrefix.Concat(new[] { command }).Concat(args).ToList();

        var (_, output) = await RunAsync(argv, timeoutSeconds).ConfigureAwait(false);
        return output;
    }

    /// <summary>The <c>max</c> counter from a cgroup's <c>pids.events</c> — how many times the kernel denied a fork for hitting <c>pids.max</c>. Null when the file is absent.</summary>
    public int? ReadPidsEventsMax(string leafPath) => ReadEventCounter(leafPath, "pids.events", "max");

    /// <summary>The <c>oom_kill</c> counter from a cgroup's <c>memory.events</c> — how many processes the cgroup OOM killer terminated for hitting <c>memory.max</c>. Null when the file is absent.</summary>
    public int? ReadMemoryEventsOomKill(string leafPath) => ReadEventCounter(leafPath, "memory.events", "oom_kill");

    /// <summary>The raw <c>cpu.max</c> content of a leaf (e.g. <c>"50000 100000"</c>) — proves the cpu controller was enabled + the quota written on the real kernel. Null when absent.</summary>
    public string? ReadCpuMax(string leafPath)
    {
        try { return File.ReadAllText(Path.Combine(leafPath, "cpu.max")).Trim(); }
        catch { return null; }
    }

    private static int? ReadEventCounter(string leafPath, string file, string key)
    {
        try
        {
            foreach (var line in File.ReadAllLines(Path.Combine(leafPath, file)))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[0] == key && int.TryParse(parts[1], out var n)) return n;
            }
        }
        catch { /* absent / unreadable */ }

        return null;
    }

    public void Dispose()
    {
        try
        {
            // Reap every capped leaf (kill → drain → rmdir) so none leaks; _cs-host is intentionally left (it holds the
            // live container processes — killing it would kill this test host).
            foreach (var leaf in Directory.EnumerateDirectories(Root))
                ReapLeaf(leaf);

            Directory.Delete(Root);
        }
        catch { /* best-effort — the container is ephemeral anyway */ }
    }

    private static void ReapLeaf(string leaf)
    {
        try
        {
            WriteFile(Path.Combine(leaf, "cgroup.kill"), "1");

            var deadline = Environment.TickCount64 + DrainTimeoutMs;
            while (ProcCount(leaf) > 0 && Environment.TickCount64 < deadline) Thread.Sleep(50);

            Directory.Delete(leaf);
        }
        catch { /* best-effort */ }
    }

    private static int ProcCount(string leaf)
    {
        try { return File.ReadAllLines(Path.Combine(leaf, "cgroup.procs")).Count(l => l.Trim().Length > 0); }
        catch { return 0; }
    }

    // ─── cgroup-v2 bootstrap ───

    private static string? CurrentCgroupDir()
    {
        try
        {
            // cgroup-v2 unified line is "0::<path>"; the absolute dir is the mount + that path.
            var rel = File.ReadAllLines("/proc/self/cgroup")
                .Select(l => l.Split("::", 2))
                .Where(p => p.Length == 2 && p[0] == "0")
                .Select(p => p[1].Trim())
                .FirstOrDefault();

            if (rel is null) return null;

            var dir = rel is "/" or "" ? CgroupMount : CgroupMount + rel;
            return Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    private static bool ControllersAvailable(string dir, out string missing)
    {
        missing = "";
        try
        {
            var available = File.ReadAllText(Path.Combine(dir, "cgroup.controllers")).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var need = Controllers.Where(c => !available.Contains(c)).ToList();
            missing = string.Join(",", need);
            return need.Count == 0;
        }
        catch (Exception ex) { missing = ex.Message; return false; }
    }

    /// <summary>Enable memory+cpu+pids on <paramref name="dir"/>/cgroup.subtree_control — directly when possible, else leaf-ify (move the cgroup's processes into a sibling leaf so it has no internal processes) and retry.</summary>
    private static bool TryEnableControllers(string dir, out string error)
    {
        error = "";

        if (AlreadyEnabled(dir)) return true;

        if (TryWrite(Path.Combine(dir, "cgroup.subtree_control"), EnableDirective, out error)) return true;

        // EBUSY: the cgroup has internal processes + is not the namespace root → move them into a leaf, then retry.
        if (!TryLeafifyProcesses(dir, out var leafErr)) { error = $"{error}; leafify: {leafErr}"; return false; }

        return TryWrite(Path.Combine(dir, "cgroup.subtree_control"), EnableDirective, out error);
    }

    private static bool AlreadyEnabled(string dir)
    {
        try
        {
            var enabled = File.ReadAllText(Path.Combine(dir, "cgroup.subtree_control")).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return Controllers.All(c => enabled.Contains(c));
        }
        catch { return false; }
    }

    private static bool TryLeafifyProcesses(string dir, out string error)
    {
        error = "";
        try
        {
            var host = Path.Combine(dir, "_cs-host");
            Directory.CreateDirectory(host);

            var hostProcs = Path.Combine(host, "cgroup.procs");
            foreach (var pid in File.ReadAllLines(Path.Combine(dir, "cgroup.procs")).Where(l => l.Trim().Length > 0))
                try { File.WriteAllText(hostProcs, pid.Trim()); } catch { /* a pid may exit between read + move; skip it */ }

            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static bool TryWrite(string path, string value, out string error)
    {
        error = "";
        try { File.WriteAllText(path, value); return true; }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static void WriteFile(string path, string value)
    {
        if (File.Exists(path)) File.WriteAllText(path, value);
    }

    private static bool ProbePython()
    {
        try { return RunAsync(new[] { "python3", "--version" }, 10).GetAwaiter().GetResult().Exit == 0; }
        catch { return false; }
    }

    private static async Task<(int Exit, string Output)> RunAsync(IReadOnlyList<string> argv, int timeoutSeconds)
    {
        var psi = new ProcessStartInfo { FileName = argv[0], RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in argv.Skip(1)) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ } return (124, "[timed out]"); }

        return (p.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }
}
