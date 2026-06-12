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

    /// <summary>Wall-clock cap. On expiry the runner terminates the command (and its children) and returns <see cref="SandboxStatus.TimedOut"/>.</summary>
    public int TimeoutSeconds { get; init; } = 600;

    /// <summary>
    /// Names of environment variables that must point the command at a PER-RUN ISOLATED config home rather
    /// than the operator's personal dotfiles under <c>$HOME</c>. The durable runner creates a fresh directory
    /// under the run's spool dir and sets every name here to it, so a shelled-out CLI (Claude Code's
    /// <c>CLAUDE_CONFIG_DIR</c>, Codex's <c>CODEX_HOME</c>) reads ONLY the credentials/config we inject — never
    /// the operator's <c>~/.claude</c> / <c>~/.codex</c>, whose base-URL / proxy overrides would otherwise
    /// hijack the run. Empty → the command needs no config isolation.
    /// </summary>
    public IReadOnlyList<string> ConfigHomeEnvVars { get; init; } = Array.Empty<string>();
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
}
