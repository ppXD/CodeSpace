using System.Diagnostics;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// Per-run resource caps for a sandboxed command, via the util-linux <c>prlimit</c> wrapper (Linux): the command
/// is rewritten <c>prlimit --nproc=N --fsize=BYTES -- &lt;command&gt; &lt;args&gt;</c>, so the kernel enforces
/// <c>RLIMIT_NPROC</c> (a fork-bomb cap) and <c>RLIMIT_FSIZE</c> (a single-file / runaway-stdout-spool size cap) on
/// the agent and every descendant. Composes OUTSIDE bubblewrap (prlimit sets the rlimits, then execs bwrap, which
/// inherits them). Chosen over the shell's <c>ulimit</c> because dash — the default <c>/bin/sh</c> — has no
/// <c>ulimit -u</c>, so the cap would silently no-op; <c>prlimit</c> is shell-agnostic with explicit byte units.
///
/// <para>Probed (<see cref="Available"/>): Linux + a working <c>prlimit</c>. Absent (macOS dev / no prlimit) → no
/// caps, the documented unconfined trust mode. A memory-RSS cap + a total-disk quota need cgroup delegation — a
/// later slice; this covers the fork-bomb + runaway-file gaps without it.</para>
/// </summary>
public static class ProcessRlimits
{
    /// <summary>Operator override for the fork-bomb process cap (RLIMIT_NPROC); overrides <see cref="SandboxSpec.MaxProcesses"/>. Pinned by a test (Rule 8).</summary>
    public const string MaxProcessesEnvVar = "CODESPACE_AGENT_MAX_PROCESSES";

    /// <summary>Operator override for the single-file size cap in MiB (RLIMIT_FSIZE); overrides <see cref="SandboxSpec.MaxFileSizeMb"/>. Pinned by a test (Rule 8).</summary>
    public const string MaxFileSizeMbEnvVar = "CODESPACE_AGENT_MAX_FILE_MB";

    private const string DefaultCommand = "prlimit";

    private const int ProbeTimeoutMs = 3000;

    private static readonly Lazy<string?> LazyAvailable = new(Probe);

    /// <summary>The resolved <c>prlimit</c> path when this host can cap resources (Linux + working prlimit), else <c>null</c>.</summary>
    public static string? Available => LazyAvailable.Value;

    /// <summary>The effective process cap: the operator env override (<see cref="MaxProcessesEnvVar"/>) when set, else the spec's value.</summary>
    public static int EffectiveMaxProcesses(int specValue) => Resolve(MaxProcessesEnvVar, specValue);

    /// <summary>The effective single-file size cap in MiB: the operator env override (<see cref="MaxFileSizeMbEnvVar"/>) when set, else the spec's value.</summary>
    public static int EffectiveMaxFileSizeMb(int specValue) => Resolve(MaxFileSizeMbEnvVar, specValue);

    /// <summary>
    /// Pure: rewrite <paramref name="command"/> as <c>prlimit --nproc=N --fsize=BYTES -- command args</c>, including
    /// only the positive caps. Returns (command, args) UNCHANGED when both caps are non-positive (no cap requested).
    /// </summary>
    public static (string Command, IReadOnlyList<string> Args) Wrap(string prlimit, string command, IReadOnlyList<string> args, int maxProcesses, int maxFileMb)
    {
        if (maxProcesses <= 0 && maxFileMb <= 0) return (command, args);

        var a = new List<string>();
        if (maxProcesses > 0) a.Add($"--nproc={maxProcesses}");
        if (maxFileMb > 0) a.Add($"--fsize={(long)maxFileMb * 1024 * 1024}");
        a.Add("--");
        a.Add(command);
        a.AddRange(args);

        return (prlimit, a);
    }

    private static int Resolve(string envVar, int specValue) =>
        Environment.GetEnvironmentVariable(envVar) is { Length: > 0 } v && int.TryParse(v, out var parsed) ? parsed : specValue;

    private static string? Probe()
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            var psi = new ProcessStartInfo { FileName = DefaultCommand, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            psi.ArgumentList.Add("--version");

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            if (!proc.WaitForExit(ProbeTimeoutMs)) { try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ } return null; }

            return proc.ExitCode == 0 ? DefaultCommand : null;
        }
        catch
        {
            return null;
        }
    }
}
