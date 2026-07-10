namespace CodeSpace.Messages.Agents;

/// <summary>
/// One sandbox invocation. Kept minimal: a command + args, an optional working directory, extra
/// environment variables, and a wall-clock timeout. The runner maps these onto whatever its backend
/// needs (a local process, a container exec, a Job pod).
/// </summary>
public sealed record SandboxSpec
{
    /// <summary>Executable to run (resolved on the runner's PATH, or an absolute path). Never passed through a shell — args are explicit.</summary>
    public required string Command { get; init; }

    /// <summary>Arguments passed verbatim (no shell-splitting / globbing). Each element is one argv entry.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Working directory for the command. <c>null</c> → the runner's default (current directory for the local runner).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra environment variables layered onto the runner's own environment. Secrets belong here, never in <see cref="Args"/>.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Wall-clock cap. On expiry the runner terminates the command (and its children) and returns <see cref="SandboxStatus.TimedOut"/>. <c>null</c> (or ≤0) ⇒ NO wall-clock — the run is bounded only by caller cancellation, the stall watchdog, and (for an agent run) the cost cap.</summary>
    public int? TimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Names of environment variables that must point the command at a PER-RUN ISOLATED config home rather
    /// than the operator's personal dotfiles under <c>$HOME</c>. The durable runner creates a fresh directory
    /// under the run's spool dir and sets every name here to it, so a shelled-out CLI (Claude Code's
    /// <c>CLAUDE_CONFIG_DIR</c>, Codex's <c>CODEX_HOME</c>) reads ONLY the credentials/config we inject — never
    /// the operator's <c>~/.claude</c> / <c>~/.codex</c>, whose base-URL / proxy overrides would otherwise
    /// hijack the run. Empty → the command needs no config isolation.
    /// </summary>
    public IReadOnlyList<string> ConfigHomeEnvVars { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Whether the command may reach the network. <c>false</c> → the sandbox runner severs egress entirely (a fresh
    /// network namespace with only loopback), so a confined agent cannot reach cloud-metadata, the LAN, or exfiltrate
    /// over the internet. <c>true</c> (default — non-breaking) → the host network is shared, UNLESS
    /// <see cref="EgressAllowlist"/> narrows it. Enforced only by a sandboxing runner; a bare-process runner cannot
    /// honour it.
    /// </summary>
    public bool AllowNetwork { get; init; } = true;

    /// <summary>
    /// A DENY-BY-DEFAULT egress allowlist (host names) the run may reach — the narrowing of <see cref="AllowNetwork"/>
    /// from "any host" to ONLY these (e.g. the model API + the git host). Null / empty ⇒ no allowlist: with
    /// <see cref="AllowNetwork"/> the egress is full (today's behaviour), without it severed. When an allowlist IS set
    /// but the runner cannot ENFORCE it (no privileged netns/filter — the enforcement is a follow-up slice), egress is
    /// FAIL-CLOSED severed entirely rather than left wide open, so a requested narrowing never silently degrades to
    /// "any host". The host filtering itself (netns + nftables / proxy) is enforced by a later sandbox slice.
    /// </summary>
    public IReadOnlyList<string>? EgressAllowlist { get; init; }

    /// <summary>
    /// Max processes the command + its descendants may spawn (RLIMIT_NPROC) — a fork-bomb cap so a runaway agent
    /// cannot exhaust the worker's process table. <c>0</c> = unlimited. Enforced by a sandboxing runner.
    /// </summary>
    public int MaxProcesses { get; init; } = 4096;

    /// <summary>
    /// Max size of any single file the command may write, in MiB (RLIMIT_FSIZE) — bounds a runaway file / log / the
    /// run's own stdout spool from filling the disk. <c>0</c> = unlimited. Enforced by a sandboxing runner. (A total
    /// disk quota needs cgroup io delegation — a later slice; the memory + cpu caps below land via cgroup.)
    /// </summary>
    public int MaxFileSizeMb { get; init; } = 2048;

    /// <summary>
    /// Max resident memory the command + every descendant may use, in MiB — a cgroup-v2 <c>memory.max</c> cap so a
    /// runaway agent (or its subtree) cannot OOM the worker. This is the capability prlimit cannot give: <c>RLIMIT_AS</c>
    /// is per-process address space, not a whole-subtree RSS ceiling. <c>0</c> = unlimited (the byte-identical default).
    /// Enforced only by a runner with cgroup-v2 delegation (the durable local runner on Linux); ignored otherwise. The
    /// PURE limit plan is <c>CgroupResourcePlan</c>; the privileged executor + lifecycle wiring land in a later slice.
    /// </summary>
    public int MaxMemoryMb { get; init; }

    /// <summary>
    /// Max CPU the command + every descendant may use, as a percent of ONE core — a cgroup-v2 <c>cpu.max</c> quota
    /// (e.g. <c>50</c> ⇒ "50000 100000", 50% of a core; <c>200</c> allows two cores). <c>0</c> = unlimited (the default).
    /// Enforced only by a runner with cgroup-v2 delegation; ignored otherwise.
    /// </summary>
    public int MaxCpuPercent { get; init; }

    /// <summary>
    /// MCP tool-fabric wiring for this run: the declaration the runner writes into the per-run config-home (0600 — it
    /// carries the run token) plus the socket it binds into the sandbox so the harness's <c>codespace-mcp</c> proxy can
    /// reach this run's endpoint. The executor sets it AFTER opening the run's endpoint (it owns the minted token +
    /// resolved socket path); a pure <c>IAgentHarness.BuildInvocation</c> can't, since the values are run-scoped.
    /// <c>null</c> (default) → no tool fabric, byte-identical to a run without it. Honoured only by a runner that writes
    /// a per-run config-home (the durable local runner); a bare-process runner ignores it.
    /// </summary>
    public McpServerWiring? Mcp { get; init; }

    /// <summary>
    /// Files the runner materializes into the per-run config home (<see cref="ConfigHomeEnvVars"/>) BEFORE launch — e.g.
    /// a projected skill's <c>skills/&lt;slug&gt;/SKILL.md</c>, which the harness's CLI then discovers natively. Unlike the
    /// run-scoped <see cref="Mcp"/> declaration (injected by the executor because it carries a minted token), these are a
    /// PURE function of the task, so the harness's <c>BuildInvocation</c> emits them. Not secrets — written with default
    /// perms. Empty (default) → nothing written, byte-identical to a run without them. Honoured only by a runner that
    /// writes a per-run config home; a bare-process runner with no config home ignores them.
    /// </summary>
    public IReadOnlyList<ConfigHomeFile> ConfigHomeFiles { get; init; } = Array.Empty<ConfigHomeFile>();
}

/// <summary>One file to write into the per-run config home, its <see cref="RelativePath"/> joined onto the config-home dir.</summary>
public sealed record ConfigHomeFile
{
    /// <summary>Path relative to the config home (e.g. "skills/test-driven-development/SKILL.md"). Forward-slashed; the runner joins it onto the config-home dir and creates intermediate directories.</summary>
    public required string RelativePath { get; init; }

    public required string Content { get; init; }

    /// <summary>
    /// True for a file the harness's own config invokes by direct command path (e.g. the Stop-hook script wired as
    /// <c>"$CLAUDE_CONFIG_DIR"/hooks/…</c>) — the shell execs the FILE, so it needs +x or the invocation dies with
    /// exit 126 and the hook silently never runs. False (default) for plain config/content files.
    /// </summary>
    public bool IsExecutable { get; init; }
}

/// <summary>
/// Outcome of a sandbox run. A runner that streams (<see cref="ISandboxStreamRunner"/>) delivers stdout
/// live line-by-line and leaves <see cref="Stdout"/> empty; the non-streaming <see cref="ISandboxRunner.RunAsync"/>
/// path returns it buffered in full. A stderr size cap for chatty long runs is a future sibling capability.
/// </summary>
public sealed record SandboxResult
{
    public required SandboxStatus Status { get; init; }

    /// <summary>Process exit code. <c>-1</c> when the command was terminated before a natural exit (timeout).</summary>
    public required int ExitCode { get; init; }

    /// <summary>Buffered stdout from the non-streaming path; empty when the run streamed line-by-line (the lines were delivered live).</summary>
    public required string Stdout { get; init; }

    public required string Stderr { get; init; }
}

/// <summary>Terminal state of a sandbox run.</summary>
public enum SandboxStatus
{
    /// <summary>Exited with code 0.</summary>
    Success,

    /// <summary>Exited with a non-zero code.</summary>
    Failed,

    /// <summary>Did not finish within <see cref="SandboxSpec.TimeoutSeconds"/> and was terminated.</summary>
    TimedOut,

    /// <summary>Slice C3: produced NO output for the configured idle window and was terminated early — the run is stalled (e.g. a nested tool waiting at an interactive prompt the agent can't answer, a deadlock). Distinct from <see cref="TimedOut"/> (a run that was making progress but ran past its budget): a stall is surfaced for a human as NeedsReview(Blocked), faster than the full timeout. Only ever produced when the idle watchdog is enabled.</summary>
    Stalled,
}
