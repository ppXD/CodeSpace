using System.Diagnostics;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// OS-level filesystem + process-namespace confinement for a sandboxed agent child, via Linux
/// <c>bubblewrap</c> (<c>bwrap</c>) using the COMMAND-REWRITE strategy: the original command is rewritten as
/// <c>bwrap &lt;confinement args&gt; -- &lt;command&gt; &lt;args&gt;</c> (see <see cref="BuildArgs"/>). The agent then runs in a
/// fresh mount / pid / ipc / uts / user / cgroup namespace over a READ-ONLY minimal root (the standard system dirs
/// in <see cref="ReadOnlyRootDirs"/>), with EVERY Linux capability dropped (<c>--cap-drop ALL</c>), a fresh
/// <c>/proc</c> + <c>/dev</c> + a tmpfs <c>/tmp</c>, and the ONLY writable host paths being THIS run's workspace +
/// config-home. So a prompt-injected or malicious agent cannot read the operator's <c>~/.ssh</c> / <c>~/.aws</c> /
/// <c>~/.config/gh</c>, cannot see other runs' clones or spools, cannot write outside its workspace, and cannot
/// wield any capability — closing the audit's critical filesystem gaps.
///
/// <para><b>Confinement surface:</b> mount + pid + ipc + uts + user + cgroup namespaces, all capabilities dropped,
/// and (via the runner's durable launch) a deny-by-default egress allowlist + cgroup CPU/memory caps. Seccomp
/// syscall filtering is the remaining hardening.</para>
///
/// <para><b>Availability is probed, never assumed</b> (<see cref="Available"/>): Linux + a <c>bwrap</c> binary +
/// a WORKING unprivileged user namespace (a real confined <c>true</c> must exit 0). When unavailable — macOS dev,
/// no <c>bwrap</c>, or a host that forbids unprivileged userns — the caller runs the command UNCONFINED. That is
/// an honestly-degraded trust mode that must be surfaced, never silently presented as isolation.</para>
/// </summary>
public static class BubblewrapSandbox
{
    /// <summary>Operator override for the bwrap binary (absolute path or a PATH name). Pinned by a test (Rule 8).</summary>
    public const string CommandEnvVar = "CODESPACE_BWRAP_PATH";

    /// <summary>
    /// Set to <c>1</c>/<c>true</c> to FAIL-CLOSED: when sandboxing is unavailable the runner refuses to launch the
    /// agent unconfined (rather than silently degrading). A multi-tenant / untrusted-input deployment sets this so a
    /// missing primitive surfaces as a failed run, never a silent isolation hole. Pinned by a test (Rule 8).
    /// </summary>
    public const string RequireSandboxEnvVar = "CODESPACE_REQUIRE_SANDBOX";

    private const string DefaultCommand = "bwrap";

    private const int ProbeTimeoutMs = 5000;

    /// <summary>
    /// The standard read-only system roots bound into the sandbox (bind-IF-PRESENT) so the agent's runtime and the
    /// harness binary stay reachable while the rest of the host filesystem is invisible. Excludes <c>/home</c>,
    /// <c>/root</c>, <c>/var</c>, <c>/tmp</c>, <c>/mnt</c>, <c>/media</c> — where operator secrets + other tenants live.
    /// Pinned by a test (Rule 8): widening this re-exposes host paths to the untrusted agent, a reviewed decision.
    /// </summary>
    public static readonly IReadOnlyList<string> ReadOnlyRootDirs = new[]
    {
        "/usr", "/bin", "/sbin", "/lib", "/lib64", "/lib32", "/etc", "/opt",
    };

    private static readonly Lazy<string?> LazyAvailable = new(Probe);

    /// <summary>The resolved <c>bwrap</c> path when this host can confine (Linux + bwrap + working userns), else <c>null</c>.</summary>
    public static string? Available => LazyAvailable.Value;

    /// <summary>Whether this deployment mandates confinement (<see cref="RequireSandboxEnvVar"/>) — read live so an operator can flip it without a code change.</summary>
    public static bool IsRequired => Environment.GetEnvironmentVariable(RequireSandboxEnvVar) is "1" or "true" or "TRUE";

    /// <summary>
    /// Fail-closed guard: throws when confinement is <paramref name="required"/> but <paramref name="available"/> is
    /// null, so a deployment that mandates isolation never silently runs an agent unconfined. Pure (explicit args)
    /// so it is unit-testable without touching process env.
    /// </summary>
    public static void EnsureSatisfiable(string? available, bool required)
    {
        if (required && available is null)
            throw new InvalidOperationException(
                $"Sandbox isolation is required ({RequireSandboxEnvVar}=1) but bubblewrap is unavailable on this host " +
                "(not Linux, bwrap not installed, or unprivileged user namespaces denied). Refusing to run the agent unconfined.");
    }

    /// <summary>
    /// Build the bwrap argument vector that confines <paramref name="plan"/>'s command: a read-only minimal root,
    /// fresh proc/dev/tmpfs-tmp, the plan's writable paths rw-bound at their real paths, HOME redirected into the
    /// sandbox, chdir'd into the working directory, then <c>-- command args…</c>. PURE (no process, no probe) so it
    /// is unit-testable on any OS; the caller prepends <see cref="Available"/> as the executable.
    /// </summary>
    public static IReadOnlyList<string> BuildArgs(BwrapPlan plan)
    {
        var args = new List<string>
        {
            // Tie the sandbox lifetime to the launcher, and isolate every namespace we can unprivileged.
            "--die-with-parent",
            "--unshare-user", "--unshare-pid", "--unshare-ipc", "--unshare-uts",
            "--unshare-cgroup-try",          // private cgroup namespace → agent sees 0::/, not its real cgroup leaf path (best-effort: -try keeps full confinement on a pre-cgroupns kernel)
            "--cap-drop", "ALL",             // drop EVERY Linux capability, even userns-local ones → mount / module-load / cap-requiring ops all denied inside the namespace
            "--new-session",                 // own session → blocks TIOCSTI tty-injection back to the parent
            "--proc", "/proc",               // fresh procfs → can't scrape other host processes / their environ
            "--dev", "/dev",                 // minimal devtmpfs (null/zero/random/…)
            "--tmpfs", "/tmp",               // private /tmp → host /tmp (other runs' spools) shadowed
        };

        // Network: the egress policy decides the namespace. None (network forbidden) OR a requested allowlist this
        // runner cannot yet ENFORCE (canEnforceAllowlist:false — the privileged host-filter is a later slice) both
        // FAIL CLOSED to --unshare-net (a fresh net namespace, loopback only — no cloud-metadata / LAN / internet).
        // Only Full shares the host network (the agent reaches its model API). Byte-identical for a run with no
        // allowlist: ShareNetwork true → Full → shared; false → None → severed.
        var egress = SandboxEgressPolicy.Derive(plan.ShareNetwork, plan.EgressAllowlist, canEnforceAllowlist: false);
        if (egress.Mode != SandboxEgressMode.Full) args.Add("--unshare-net");

        // Read-only minimal root: the runtime + harness binary are reachable, the rest of the host FS is invisible.
        foreach (var dir in ReadOnlyRootDirs)
        {
            args.Add("--ro-bind-try");
            args.Add(dir);
            args.Add(dir);
        }

        // If the command is an absolute path outside the standard roots (an operator binary override), bind its dir
        // read-only so it stays reachable inside the otherwise-minimal root.
        if (Path.IsPathRooted(plan.Command) && Path.GetDirectoryName(plan.Command) is { Length: > 0 } cmdDir && !IsUnderReadOnlyRoot(cmdDir))
        {
            args.Add("--ro-bind-try");
            args.Add(cmdDir);
            args.Add(cmdDir);
        }

        // Extra read-only dirs reachable inside the sandbox (the codespace-mcp proxy binary's dir, so the harness can
        // spawn it at its absolute identity-bound path). --ro-bind-try (not a hard bind) so a missing dir never crashes
        // bwrap. Empty by default → no extra ro-bind, byte-identical to a run without the tool fabric.
        foreach (var dir in DistinctNonEmpty(plan.ReadOnlyExtraPaths))
        {
            args.Add("--ro-bind-try");
            args.Add(dir);
            args.Add(dir);
        }

        // The ONLY writable host paths: this run's workspace + config-home, bound at their real paths so absolute
        // refs (CLAUDE_CONFIG_DIR, the workspace) still resolve. Applied AFTER --tmpfs /tmp so a path under /tmp
        // re-surfaces the real dir over the tmpfs.
        foreach (var path in DistinctNonEmpty(plan.WritablePaths))
        {
            args.Add("--bind");
            args.Add(path);
            args.Add(path);
        }

        // HOME must point somewhere bound + writable inside the sandbox (the operator's real home is NOT bound), so
        // ~-relative reads (~/.ssh, ~/.aws, ~/.netrc) miss instead of leaking. Default to /tmp (tmpfs) when no
        // config-home was supplied.
        args.Add("--setenv");
        args.Add("HOME");
        args.Add(string.IsNullOrEmpty(plan.HomeDir) ? "/tmp" : plan.HomeDir);

        if (!string.IsNullOrEmpty(plan.WorkingDirectory))
        {
            args.Add("--chdir");
            args.Add(plan.WorkingDirectory);
        }

        args.Add("--");
        args.Add(plan.Command);
        args.AddRange(plan.Args);

        return args;
    }

    private static bool IsUnderReadOnlyRoot(string dir) =>
        ReadOnlyRootDirs.Any(root => dir == root || dir.StartsWith(root + "/", StringComparison.Ordinal));

    private static IEnumerable<string> DistinctNonEmpty(IEnumerable<string> paths)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in paths)
            if (!string.IsNullOrEmpty(p) && seen.Add(p)) yield return p;
    }

    /// <summary>
    /// Resolve bwrap only on Linux, and only if a TRIVIAL confined process actually runs — a host that forbids
    /// unprivileged user namespaces (restrictive sysctl / seccomp) has bwrap on PATH but cannot confine, so we must
    /// fall back to unconfined rather than fail every run. Result is cached for the process lifetime.
    /// </summary>
    private static string? Probe()
    {
        if (!OperatingSystem.IsLinux()) return null;

        var path = Environment.GetEnvironmentVariable(CommandEnvVar) is { Length: > 0 } p ? p : DefaultCommand;

        try
        {
            // Exercise the flags the real run depends on (--cap-drop, --unshare-cgroup-try), not just userns: a bwrap
            // too old to know them must report UNAVAILABLE (→ unconfined fallback / fail-closed) rather than pass here
            // and then die on every real launch with "unknown option". --unshare-cgroup-try is best-effort, so a host
            // without cgroup namespaces still probes clean.
            var psi = new ProcessStartInfo { FileName = path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "--unshare-user", "--unshare-pid", "--unshare-cgroup-try", "--cap-drop", "ALL", "--ro-bind", "/", "/", "--", "true" })
                psi.ArgumentList.Add(arg);

            using var proc = Process.Start(psi);
            if (proc is null) return null;

            if (!proc.WaitForExit(ProbeTimeoutMs))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return null;
            }

            return proc.ExitCode == 0 ? path : null;
        }
        catch
        {
            return null;   // bwrap absent / not executable / userns denied → unconfined fallback
        }
    }
}

/// <summary>Inputs to <see cref="BubblewrapSandbox.BuildArgs"/> — the command to confine plus the run's writable paths, working dir, and sandbox HOME.</summary>
public sealed record BwrapPlan
{
    public required string Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Directory the sandboxed command starts in (bound writable via <see cref="WritablePaths"/>).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Value for the sandbox's HOME — a bound, writable path (the per-run config-home); <c>/tmp</c> when null.</summary>
    public string? HomeDir { get; init; }

    /// <summary>Host paths bound READ-WRITE into the sandbox (this run's workspace + config-home) — the only writable host paths.</summary>
    public IReadOnlyList<string> WritablePaths { get; init; } = Array.Empty<string>();

    /// <summary>Host dirs bound READ-ONLY (<c>--ro-bind-try</c>) so a needed binary stays reachable at its absolute path — the <c>codespace-mcp</c> proxy's dir. Init-only, defaulted empty → non-breaking.</summary>
    public IReadOnlyList<string> ReadOnlyExtraPaths { get; init; } = Array.Empty<string>();

    /// <summary>Whether to SHARE the host network. <c>false</c> → <c>--unshare-net</c> (only loopback; no egress).</summary>
    public bool ShareNetwork { get; init; } = true;

    /// <summary>The deny-by-default egress allowlist (host names) — narrows <see cref="ShareNetwork"/> to ONLY these. Null / empty ⇒ no allowlist. Until the privileged host-filtering slice lands the allowlist is UNENFORCEABLE here, so a set allowlist FAILS CLOSED to no egress (see <see cref="SandboxEgressPolicy"/>).</summary>
    public IReadOnlyList<string>? EgressAllowlist { get; init; }
}
