namespace CodeSpace.Messages.Agents;

/// <summary>
/// A declarative request to run ONE command in a sandbox, optionally inside a freshly-cloned repository
/// workspace. The substrate-neutral envelope behind the <c>agent.run_command</c> node — it names WHAT to
/// run and the isolation posture, never a runner detail, so the same request drives the local runner today
/// and a future docker / k8s runner unchanged (selected by <see cref="RunnerKind"/>).
///
/// <para>Isolation is secure-by-default: <see cref="AllowNetwork"/> is <c>false</c> (severed egress) and the
/// rlimit caps are pre-set, so an arbitrary command runs confined unless the author explicitly opens it up —
/// the opposite default from <c>SandboxSpec</c> (whose agent-driven default shares the host network).</para>
/// </summary>
public sealed record RunCommandRequest
{
    /// <summary>Repository to clone into a fresh per-run workspace the command runs in. <c>null</c> → an ephemeral run with no checkout (the runner's default working dir).</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>
    /// The team that owns the run — the repository load is fail-closed to it: a <see cref="RepositoryId"/> resolves
    /// only when it belongs to this team, so a model-supplied (or otherwise untrusted) id can never reach another
    /// tenant's repository. <c>null</c> together with a <see cref="RepositoryId"/> is refused (no team context → no
    /// clone). Ignored for an ephemeral (no-repo) run.
    /// </summary>
    public Guid? TeamId { get; init; }

    /// <summary>Branch / tag / sha to check out (repo-scoped only). <c>null</c> → the repository's default branch.</summary>
    public string? Ref { get; init; }

    /// <summary>Executable to run (resolved on PATH, or absolute). Never shell-interpreted — args are explicit.</summary>
    public required string Command { get; init; }

    /// <summary>Arguments passed verbatim (no shell-splitting / globbing). Each element is one argv entry.</summary>
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>Extra environment variables layered onto the command's environment.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Wall-clock cap. On expiry the runner terminates the command (and its children) → <c>TimedOut</c>.</summary>
    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>Whether the command may reach the network. <c>false</c> (secure default) → a sandboxing runner severs egress to loopback-only.</summary>
    public bool AllowNetwork { get; init; } = false;

    /// <summary>Max processes the command + descendants may spawn (fork-bomb cap, RLIMIT_NPROC). <c>0</c> = unlimited.</summary>
    public int MaxProcesses { get; init; } = 4096;

    /// <summary>Max size of any single file the command may write, in MiB (RLIMIT_FSIZE). <c>0</c> = unlimited.</summary>
    public int MaxFileSizeMb { get; init; } = 2048;

    /// <summary>Sandbox runner + workspace backend to use — "local" (v0), later "docker" / "k8s". <c>null</c> → the deployment default ("local").</summary>
    public string? RunnerKind { get; init; }
}
