namespace CodeSpace.Core.Services.Agents.Sandbox.Isolation;

/// <summary>
/// The PURE plan for a per-run cgroup-v2 resource cap (the B4 memory/cpu/pids tier) — the capability prlimit can't
/// give: a whole-SUBTREE memory ceiling (<c>memory.max</c> + <c>memory.swap.max</c>), a CPU quota (<c>cpu.max</c>),
/// and a robust process cap (<c>pids.max</c>) on the agent and every descendant, enforced by the kernel rather than
/// per-process rlimits. It produces, as DATA: the run's cgroup path to create, the limit files + values to write, the
/// controllers the parent must enable, the self-add exec prefix the confined command runs behind (so the child places
/// ITSELF into the cgroup race-free, before <c>exec</c>), and the reap handles (<c>cgroup.kill</c> for an atomic
/// whole-subtree kill, <c>cgroup.procs</c> for the pre-5.14 fallback). The privileged executor (and its real-kernel CI
/// E2E) does the filesystem IO; this file never touches the kernel, so the limit formats + the reap + the placement
/// are unit-pinned without root — exactly the <c>FilteredEgressPlan</c> shape for egress.
///
/// <para><b>Uniqueness.</b> The leaf name is the FULL slugged <c>runId</c> (lowercased alphanumerics), NOT a truncated
/// prefix: the cgroup DIRECTORY NAME *is* the contended shared-kernel resource, so two runs that slug-collide would
/// <c>mkdir</c> the same directory and silently share one cap (run B's fork bomb charged to run A's <c>pids.max</c>;
/// reaping A's <c>cgroup.kill</c> SIGKILLs B). Unlike <c>FilteredEgressPlan</c>, which truncates to 8 chars only because
/// veth device names are capped at <c>IFNAMSIZ</c> (15) and gets its real uniqueness from a collision-free subnet
/// allocator, a cgroup name has no length pressure (≤ <c>NAME_MAX</c>), so it carries the full runId and inherits the
/// caller's run-id uniqueness directly. No GUID is appended — the name stays PURELY runId-derived so a reaper
/// reconstructs it with no setup-time state.</para>
///
/// <para><b>Executor contract (enforced in the privileged slice, documented here so the data is usable).</b> cgroup-v2
/// requires the PARENT to enable a controller in its <c>cgroup.subtree_control</c> before a child can write that
/// controller's files — a leaf <c>memory.max</c> write ENOENTs otherwise. The executor must, once, enable
/// <see cref="RequiredControllers"/> on the delegated <c>cgroupRoot</c>, then (per the "no internal processes" rule)
/// keep processes in the LEAF (<see cref="Path"/>), never the controller-enabled parent. Teardown ORDER matters: write
/// <c>"1"</c> to <see cref="KillFile"/> (or SIGKILL each <see cref="ProcsFile"/> member on a pre-5.14 kernel) and let
/// the cgroup drain BEFORE <c>rmdir</c> — <c>rmdir</c> of a non-empty cgroup fails <c>EBUSY</c> and leaks the cap.</para>
///
/// <para>cgroup-v2 orthogonality: membership is by host PID and is INHERITED across fork / unshare, so the cap holds
/// even though the command also runs inside bwrap's PID/mount/net namespaces (cgroup is a separate hierarchy). The
/// plan slots OUTERMOST in the launch chain — the self-add wrapper writes its own pid to <c>cgroup.procs</c> on the
/// host then <c>exec</c>s the rest (egress netns → prlimit → bwrap → agent), so the whole subtree is captured. A
/// <c>null</c> plan (no positive cap) means the wiring slots NO cgroup prefix — byte-identical to a run without it.</para>
/// </summary>
public sealed record CgroupResourcePlan
{
    /// <summary>The cgroup-v2 <c>period</c> (microseconds) the cpu quota is expressed against — 100ms, the kernel default. A percent-of-one-core maps to <c>percent * 1000</c> microseconds of quota per this period.</summary>
    public const int CpuPeriodMicros = 100_000;

    /// <summary>The run's leaf cgroup directory name — the FULL slugged runId (see Uniqueness), also the teardown handle.</summary>
    public required string Name { get; init; }

    /// <summary>The run's leaf cgroup directory (<c>&lt;root&gt;/&lt;name&gt;</c>) — the executor <c>mkdir</c>s it, then writes <see cref="Limits"/>.</summary>
    public required string Path { get; init; }

    /// <summary>The cgroup-v2 controllers the requested caps need (<c>memory</c> / <c>cpu</c> / <c>pids</c>) — the executor enables exactly these on the delegated parent's <c>cgroup.subtree_control</c> (as <c>+memory +cpu …</c>) before the leaf writes, else they ENOENT.</summary>
    public required IReadOnlyList<string> RequiredControllers { get; init; }

    /// <summary>The limit files + values to write into <see cref="Path"/> after creating it (e.g. <c>memory.max</c>=bytes, <c>memory.swap.max</c>=0, <c>cpu.max</c>="quota period", <c>pids.max</c>=count). Only the POSITIVE caps appear — an unset cap leaves the kernel default ("max").</summary>
    public required IReadOnlyList<CgroupLimit> Limits { get; init; }

    /// <summary>The <c>cgroup.procs</c> path — the self-add target for <see cref="ExecPrefix"/>, and the source the executor reads to SIGKILL each member on a kernel without <see cref="KillFile"/>.</summary>
    public required string ProcsFile { get; init; }

    /// <summary>The <c>cgroup.kill</c> path — write <c>"1"</c> to atomically kill every process in the cgroup (cgroup-v2 ≥ 5.14). The reaper's primary teardown; falls back to killing <see cref="ProcsFile"/> members when absent.</summary>
    public required string KillFile { get; init; }

    /// <summary>The prefix the confined command runs behind so the child places ITSELF into the cgroup before exec — <c>sh -c 'echo $$ > &lt;procs&gt; &amp;&amp; exec "$@"' cs-cgroup</c>, with the real command + args appended. Race-free: the pid is added before the agent starts, not after Process.Start. Injection-safe: <see cref="ProcsFile"/> is built from the [a-z0-9]-only slug + the operator-config root, so no shell metacharacter can reach the double-quoted <c>sh -c</c> string.</summary>
    public required IReadOnlyList<string> ExecPrefix { get; init; }

    /// <summary>
    /// Build the plan for the run's caps under the delegated <paramref name="cgroupRoot"/> (e.g. the operator's
    /// delegated cgroup-v2 subtree). Returns <c>null</c> when NO cap is positive — no cgroup is needed, mirroring
    /// <c>ProcessRlimits.Wrap</c> returning the command unchanged. A cap of <c>0</c> (or less) is omitted, leaving the
    /// kernel default for that controller. <paramref name="maxCpuPercent"/> is a percent of one core (200 ⇒ two cores).
    /// A memory cap emits <c>memory.swap.max=0</c> alongside <c>memory.max</c> so the ceiling is a true RAM hard-limit,
    /// not silently extended by host swap.
    /// </summary>
    public static CgroupResourcePlan? Build(string cgroupRoot, string runId, int maxMemoryMb, int maxCpuPercent, int maxPids)
    {
        var limits = new List<CgroupLimit>();
        var controllers = new List<string>();

        if (maxMemoryMb > 0)
        {
            controllers.Add("memory");
            limits.Add(new CgroupLimit { FileName = "memory.max", Value = ((long)maxMemoryMb * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) });
            // No swap escape — make memory.max a hard RAM ceiling. OPTIONAL: memory.swap.max only exists when the kernel
            // has swap accounting; absent it, the write would ENOENT — but with no swap to escape to, memory.max alone
            // is already a hard cap, so the executor skips it best-effort rather than failing setup closed.
            limits.Add(new CgroupLimit { FileName = "memory.swap.max", Value = "0", Optional = true });
        }

        if (maxCpuPercent > 0)
        {
            controllers.Add("cpu");
            limits.Add(new CgroupLimit { FileName = "cpu.max", Value = $"{(long)maxCpuPercent * (CpuPeriodMicros / 100)} {CpuPeriodMicros}" });
        }

        if (maxPids > 0)
        {
            controllers.Add("pids");
            limits.Add(new CgroupLimit { FileName = "pids.max", Value = maxPids.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        if (limits.Count == 0) return null;

        var path = PathFor(cgroupRoot, runId);
        var procs = path + "/cgroup.procs";

        return new CgroupResourcePlan
        {
            Name = NameFor(runId),
            Path = path,
            RequiredControllers = controllers,
            Limits = limits,
            ProcsFile = procs,
            KillFile = path + "/cgroup.kill",
            ExecPrefix = new[] { "sh", "-c", $"echo $$ > \"{procs}\" && exec \"$@\"", "cs-cgroup" },
        };
    }

    /// <summary>The run's leaf cgroup name — derived PURELY from the FULL <paramref name="runId"/> (so a reaper reconstructs it with no setup-time state, and distinct runIds never collide on the shared cgroupfs).</summary>
    public static string NameFor(string runId) => $"cs-cg-{Slug(runId)}";

    /// <summary>The run's leaf cgroup directory — <paramref name="cgroupRoot"/> joined with <see cref="NameFor"/> (forward-slash; cgroupfs is Linux-only).</summary>
    public static string PathFor(string cgroupRoot, string runId) => $"{cgroupRoot.TrimEnd('/')}/{NameFor(runId)}";

    /// <summary>The FULL lowercased-alphanumeric runId (NOT truncated — the cgroup name is the contended resource, so it must keep the runId's uniqueness). An empty/degenerate runId falls back to a fixed token so a name is always produced.</summary>
    private static string Slug(string runId)
    {
        var clean = new string((runId ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return clean.Length > 0 ? clean : "norunid";
    }
}

/// <summary>One cgroup-v2 controller file + the value to write into it (e.g. <c>memory.max</c> ← bytes). Pure data the executor writes after creating the cgroup directory.</summary>
public sealed record CgroupLimit
{
    public required string FileName { get; init; }

    public required string Value { get; init; }

    /// <summary>When true, the executor writes this best-effort — a missing controller file (e.g. <c>memory.swap.max</c> on a kernel without swap accounting) is skipped, not a fail-closed setup error. A required limit's absent file IS an error (a controller wasn't enabled).</summary>
    public bool Optional { get; init; }
}
