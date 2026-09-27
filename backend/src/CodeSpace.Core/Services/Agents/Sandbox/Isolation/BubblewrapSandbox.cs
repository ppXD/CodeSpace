using System.Diagnostics;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// OS-level filesystem + process-namespace confinement for a sandboxed agent child, via Linux
/// <c>bubblewrap</c> (<c>bwrap</c>) using the COMMAND-REWRITE strategy: the original command is rewritten as
/// <c>bwrap &lt;confinement args&gt; -- &lt;command&gt; &lt;args&gt;</c> (see <see cref="BuildArgs"/>). The agent then runs in a
/// fresh mount / pid / ipc / uts / user / cgroup namespace over a READ-ONLY minimal root (the standard system dirs
/// in <see cref="ReadOnlyRootDirs"/>), with EVERY Linux capability dropped (<c>--cap-drop ALL</c>), a fresh
/// <c>/proc</c> + <c>/dev</c> + a tmpfs <c>/tmp</c>, and the ONLY writable host paths being THIS run's workspace +
/// config-home — the workspace only when the run may write it, since a read-only run's is mounted read-only. So a
/// prompt-injected or malicious agent cannot read the operator's <c>~/.ssh</c> / <c>~/.aws</c> /
/// <c>~/.config/gh</c>, cannot see other runs' clones or spools, cannot write outside its workspace, and cannot
/// wield any capability — closing the audit's critical filesystem gaps.
///
/// <para><b>Confinement surface:</b> mount + pid + ipc + uts + user + cgroup namespaces, all capabilities dropped,
/// and (via the runner's durable launch) a deny-by-default egress allowlist + cgroup CPU/memory caps. Seccomp
/// syscall filtering is the remaining hardening.</para>
///
/// <para><b>Availability is probed, never assumed</b> (<see cref="Available"/>): Linux + a <c>bwrap</c> binary +
/// the argv a launch builds actually running (a confined <c>true</c> under <see cref="BuildArgs"/> must exit 0). When
/// unavailable — macOS dev, no <c>bwrap</c>, a host that forbids unprivileged userns, or one that denies the launch's
/// mounts or network namespace — the caller runs the command UNCONFINED. That is an honestly-degraded trust mode that
/// must be surfaced, never silently presented as isolation.</para>
/// </summary>
public static class BubblewrapSandbox
{
    /// <summary>Operator override for the bwrap binary (absolute path or a PATH name). Pinned by a test (Rule 8).</summary>
    public const string CommandEnvVar = "CODESPACE_BWRAP_PATH";

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

    private static readonly Lazy<BwrapProbeResult> LazyProbe = new(Probe);

    /// <summary>The resolved <c>bwrap</c> path when this host can confine (Linux + bwrap + the launch argv runs), else <c>null</c>.</summary>
    public static string? Available => LazyProbe.Value.Path;

    /// <summary>WHY this host cannot confine — one of <c>SandboxConfinement</c>'s reason constants — or null when it can. The probe already distinguishes the four cases; keeping the distinction is what lets a run's record say which wall it hit instead of an unactionable "unavailable".</summary>
    public static string? UnavailableReason => LazyProbe.Value.Reason;

    /// <summary>Whether this deployment mandates confinement (<c>Sandbox:RequireConfinement</c>) — read live off the bound settings so it tracks configuration, not a captured copy.</summary>
    public static bool IsRequired => CodeSpace.Core.Settings.RuntimeSettings.Current.RequireSandboxConfinement;

    /// <summary>
    /// Fail-closed guard: throws when confinement is <paramref name="required"/> but <paramref name="available"/> is
    /// null, so a deployment that mandates isolation never silently runs an agent unconfined. Pure (explicit args)
    /// so it is unit-testable without touching process env.
    /// </summary>
    public static void EnsureSatisfiable(string? available, bool required)
    {
        if (required && available is null)
            throw new InvalidOperationException(
                "Sandbox isolation is required (Sandbox:RequireConfinement) but bubblewrap is unavailable on this host " +
                "(not Linux, bwrap not installed, unprivileged user namespaces denied, or the launch's mounts or network namespace denied). Refusing to run the agent unconfined.");
    }

    /// <summary>
    /// The launch-time record of what confinement this run ACTUALLY got — the honest answer the journal, the Room and
    /// the real-model stamp all read instead of hedging. PURE (the probe's results are passed in, never read here) so
    /// every branch is unit-testable on a host that cannot confine, which is exactly the host whose branch matters.
    ///
    /// <para><paramref name="shareNetwork"/> and <paramref name="egressAllowlist"/> are what the launch passed to
    /// <see cref="BwrapPlan.ShareNetwork"/> / <see cref="BwrapPlan.EgressAllowlist"/>, and both go through the SAME
    /// <see cref="EgressFor"/> derivation <see cref="BuildArgs"/> turns into <c>--unshare-net</c> — so the record can
    /// neither claim a severance the argv did not request NOR miss one it did (an unenforceable allowlist fails
    /// closed to severed even while the launch asked to share the network).</para>
    ///
    /// <para><paramref name="sealedToBroker"/> says the shared network IS a sealed namespace whose only destination is
    /// the run's model broker: severed from everything else, so recorded as severed — and as sealed, so a reader knows
    /// the one route it kept.</para>
    /// </summary>
    public static SandboxConfinement DeriveConfinement(string? available, string? unavailableReason, bool shareNetwork, IReadOnlyList<string>? egressAllowlist, bool sealedToBroker = false)
    {
        if (available is null)
            return new SandboxConfinement { Outcome = SandboxConfinementOutcome.Unconfined, Reason = unavailableReason ?? SandboxConfinement.ReasonNoBubblewrap };

        return new SandboxConfinement { Outcome = SandboxConfinementOutcome.Confined, NetworkSevered = sealedToBroker || EgressFor(shareNetwork, egressAllowlist).Mode != SandboxEgressMode.Full, EgressSealedToBroker = sealedToBroker };
    }

    /// <summary>
    /// The ONE egress derivation for a launch's network intent — read by both the argv (<see cref="BuildArgs"/>) and
    /// the record (<see cref="DeriveConfinement"/>), so a severance can never appear in one and not the other.
    /// <c>canEnforceAllowlist: false</c> until the privileged host-filtering slice lands here: a requested allowlist
    /// this sandbox cannot enforce FAILS CLOSED to no egress, never widens to Full.
    /// </summary>
    private static SandboxEgressPolicy EgressFor(bool shareNetwork, IReadOnlyList<string>? egressAllowlist) =>
        SandboxEgressPolicy.Derive(shareNetwork, egressAllowlist, canEnforceAllowlist: false);

    /// <summary>
    /// Build the bwrap argument vector that confines <paramref name="plan"/>'s command: a read-only minimal root,
    /// fresh proc/dev/tmpfs-tmp, the plan's writable paths rw-bound at their real paths, the working directory
    /// re-mounted read-only when the plan says so, HOME redirected into the sandbox, chdir'd into the working
    /// directory, then <c>-- command args…</c>. PURE (no process, no probe) so it is unit-testable on any OS; the
    /// caller prepends <see cref="Available"/> as the executable.
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
        if (EgressFor(plan.ShareNetwork, plan.EgressAllowlist).Mode != SandboxEgressMode.Full) args.Add("--unshare-net");

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

        // The ONLY writable host paths: this run's config-home and, when it may write it, its workspace, bound at
        // their real paths so absolute refs (CLAUDE_CONFIG_DIR, the workspace) still resolve. Applied AFTER --tmpfs
        // /tmp so a path under /tmp re-surfaces the real dir over the tmpfs.
        foreach (var path in DistinctNonEmpty(plan.WritablePaths))
        {
            args.Add("--bind");
            args.Add(path);
            args.Add(path);
        }

        // A run that may only read its workspace gets it mounted read-only, so a write there fails with EROFS whatever
        // the CLI's own permission mode let through. After every --bind, because bwrap applies mounts in order: a
        // writable path at or under the workspace is covered by this one rather than reopening it. A HARD bind, not
        // -try, so a missing workspace fails the launch exactly as its writable bind would.
        if (plan.WorkingDirectoryReadOnly && !string.IsNullOrEmpty(plan.WorkingDirectory))
        {
            args.Add("--ro-bind");
            args.Add(plan.WorkingDirectory);
            args.Add(plan.WorkingDirectory);
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
    /// The plan the availability probe confines: the default tier's own launch shape, network severed, running
    /// <c>true</c>. The probe runs <see cref="BuildArgs"/> of it rather than a hand-picked subset of flags, so
    /// "available" means a launch's argv runs here. A host that masks <c>/proc</c> lets a bare user namespace through
    /// and then refuses every launch's fresh <c>--proc</c>.
    ///
    /// <para>The price of probing the default tier rather than the loosest one: a host that grants user namespaces but
    /// denies network namespaces fails this argv too, reads as <see cref="SandboxConfinement.ReasonMountsDenied"/>, and
    /// runs even its network-sharing launches unconfined, though those would have confined there.</para>
    /// </summary>
    internal static readonly BwrapPlan ProbePlan = new() { Command = "true", ShareNetwork = false };

    /// <summary>
    /// Run only after <see cref="ProbePlan"/>'s argv was refused, to name the wall it hit: a user namespace with the
    /// flags a launch depends on (<c>--cap-drop</c>, <c>--unshare-cgroup-try</c>, so a bwrap too old for them still
    /// fails here) but none of the launch's own mounts. Passing is <see cref="SandboxConfinement.ReasonMountsDenied"/>;
    /// failing too is <see cref="SandboxConfinement.ReasonNoUserNamespaces"/>. Told apart by which argv ran, never by
    /// reading bwrap's stderr.
    /// </summary>
    internal static readonly IReadOnlyList<string> UserNamespaceProbeArgs = ["--unshare-user", "--unshare-pid", "--unshare-cgroup-try", "--cap-drop", "ALL", "--ro-bind", "/", "/", "--", "true"];

    /// <summary>
    /// Resolve bwrap only on Linux, and only if the argv a launch builds actually runs a confined <c>true</c>. A host
    /// that forbids unprivileged user namespaces, or masks the <c>/proc</c> a launch mounts, has bwrap on PATH but
    /// cannot confine, so we must fall back to unconfined rather than fail every run. Result is cached for the process
    /// lifetime.
    /// </summary>
    private static BwrapProbeResult Probe() =>
        OperatingSystem.IsLinux() ? ProbeAt(ConfiguredCommand()) : BwrapProbeResult.Unavailable(SandboxConfinement.ReasonNotLinux);

    private static string ConfiguredCommand() => Environment.GetEnvironmentVariable(CommandEnvVar) is { Length: > 0 } p ? p : DefaultCommand;

    /// <summary>Probe the bwrap at <paramref name="path"/> now, uncached. <see cref="Available"/> is this, run once per process; internal so a real-kernel test can probe a host posture it staged.</summary>
    internal static BwrapProbeResult ProbeAt(string path) => Classify(path, args => RunProbe(path, args));

    /// <summary>
    /// The probe's decision, pure over <paramref name="run"/>: the launch argv first, and only when bwrap started and
    /// refused it, <see cref="UserNamespaceProbeArgs"/> to say why. The reasons are distinct because the fixes are:
    /// "install bwrap", "allow user namespaces", "unmask /proc".
    /// </summary>
    internal static BwrapProbeResult Classify(string path, Func<IReadOnlyList<string>, ProbeOutcome> run) => run(BuildArgs(ProbePlan)) switch
    {
        ProbeOutcome.Ran => BwrapProbeResult.Confining(path),
        ProbeOutcome.Missing => BwrapProbeResult.Unavailable(SandboxConfinement.ReasonNoBubblewrap),
        _ => BwrapProbeResult.Unavailable(WallTheLaunchHit(run)),
    };

    private static string WallTheLaunchHit(Func<IReadOnlyList<string>, ProbeOutcome> run) =>
        run(UserNamespaceProbeArgs) == ProbeOutcome.Ran ? SandboxConfinement.ReasonMountsDenied : SandboxConfinement.ReasonNoUserNamespaces;

    /// <summary>Run bwrap once with <paramref name="args"/>. A start that fails is <see cref="ProbeOutcome.Missing"/>; a non-zero exit or a hang past the timeout is bwrap refusing to confine.</summary>
    private static ProbeOutcome RunProbe(string path, IReadOnlyList<string> args)
    {
        try
        {
            using var proc = Process.Start(ProbeStartInfo(path, args));

            if (proc is null) return ProbeOutcome.Missing;

            if (proc.WaitForExit(ProbeTimeoutMs)) return proc.ExitCode == 0 ? ProbeOutcome.Ran : ProbeOutcome.Refused;

            try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }

            return ProbeOutcome.Refused;
        }
        catch
        {
            return ProbeOutcome.Missing;   // bwrap absent / not executable → unconfined fallback
        }
    }

    private static ProcessStartInfo ProbeStartInfo(string path, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo { FileName = path, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        return psi;
    }

    /// <summary>What one probe run of bwrap showed.</summary>
    internal enum ProbeOutcome
    {
        /// <summary>The confined <c>true</c> exited 0.</summary>
        Ran,

        /// <summary>bwrap started and refused to confine: a non-zero exit, or a hang past the probe's timeout.</summary>
        Refused,

        /// <summary>bwrap could not be started at all: absent, or not executable.</summary>
        Missing,
    }

    /// <summary>The probe's two facts kept together — the resolved path when this host confines, else the reason it does not. Cached once, so the reason costs nothing beyond the probe already run.</summary>
    internal readonly record struct BwrapProbeResult(string? Path, string? Reason)
    {
        public static BwrapProbeResult Confining(string path) => new(path, null);

        public static BwrapProbeResult Unavailable(string reason) => new(null, reason);
    }
}

/// <summary>Inputs to <see cref="BubblewrapSandbox.BuildArgs"/> — the command to confine plus the run's writable paths, working dir, and sandbox HOME.</summary>
public sealed record BwrapPlan
{
    public required string Command { get; init; }

    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Directory the sandboxed command starts in — bound writable via <see cref="WritablePaths"/>, or read-only when <see cref="WorkingDirectoryReadOnly"/>.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Whether <see cref="WorkingDirectory"/> is mounted READ-ONLY, over any writable bind of it — a run whose write scope is read-only. <c>false</c> (the default) → the argv is exactly what it was without the flag.</summary>
    public bool WorkingDirectoryReadOnly { get; init; }

    /// <summary>Value for the sandbox's HOME — a bound, writable path (the per-run config-home); <c>/tmp</c> when null.</summary>
    public string? HomeDir { get; init; }

    /// <summary>Host paths bound READ-WRITE into the sandbox (this run's config-home, and its workspace unless that is read-only) — the only writable host paths.</summary>
    public IReadOnlyList<string> WritablePaths { get; init; } = Array.Empty<string>();

    /// <summary>Host dirs bound READ-ONLY (<c>--ro-bind-try</c>) so a needed binary stays reachable at its absolute path — the <c>codespace-mcp</c> proxy's dir. Init-only, defaulted empty → non-breaking.</summary>
    public IReadOnlyList<string> ReadOnlyExtraPaths { get; init; } = Array.Empty<string>();

    /// <summary>Whether to SHARE the host network. <c>false</c> → <c>--unshare-net</c> (only loopback; no egress).</summary>
    public bool ShareNetwork { get; init; } = true;

    /// <summary>The deny-by-default egress allowlist (host names) — narrows <see cref="ShareNetwork"/> to ONLY these. Null / empty ⇒ no allowlist. Until the privileged host-filtering slice lands the allowlist is UNENFORCEABLE here, so a set allowlist FAILS CLOSED to no egress (see <see cref="SandboxEgressPolicy"/>).</summary>
    public IReadOnlyList<string>? EgressAllowlist { get; init; }
}
