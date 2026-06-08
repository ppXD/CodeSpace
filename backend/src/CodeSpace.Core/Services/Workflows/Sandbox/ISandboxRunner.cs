namespace CodeSpace.Core.Services.Workflows.Sandbox;

/// <summary>
/// Runs one command inside an isolated execution sandbox and returns its outcome. This is the seam
/// that lets the agent layer stay deployment-neutral: v0 ships <see cref="LocalProcessRunner"/>
/// (an OS process on the worker), and a Docker / Kubernetes-Job / remote runner each land later as
/// their own <c>ISandboxRunner</c> with a distinct <see cref="Kind"/> — no consumer changes.
///
/// The interface stays narrow on purpose (Rule 7 / ISP): one method that runs a command and returns
/// its result. Richer capabilities — streaming logs, an interactive session a harness drives over
/// many turns, file upload/extraction — land as SIBLING interfaces, so existing runners never grow
/// new members to "fit" the contract.
///
/// An implementation MUST be stateless and safe for concurrent invocations: the registry resolves a
/// single instance and many runs may execute against it at once.
/// </summary>
public interface ISandboxRunner
{
    /// <summary>Stable runner tag. Convention: lowercase, e.g. "local" (v0), "docker", "k8s". Matches the key the registry resolves by.</summary>
    string Kind { get; }

    /// <summary>
    /// Run <paramref name="spec"/> to completion and return its <see cref="SandboxResult"/>. A
    /// non-zero exit is an expected outcome (<see cref="SandboxStatus.Failed"/>), NOT an exception;
    /// exceeding <see cref="SandboxSpec.TimeoutSeconds"/> yields <see cref="SandboxStatus.TimedOut"/>.
    /// Throws only for infrastructure failures and when <paramref name="cancellationToken"/> is
    /// cancelled by the caller (distinct from the spec's own timeout).
    /// </summary>
    Task<SandboxResult> RunAsync(SandboxSpec spec, CancellationToken cancellationToken);
}

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
}

/// <summary>Outcome of a sandbox run. Output is captured in full in v0; a future slice adds streaming + size caps for long agent runs.</summary>
public sealed record SandboxResult
{
    public required SandboxStatus Status { get; init; }

    /// <summary>Process exit code. <c>-1</c> when the command was terminated before a natural exit (timeout).</summary>
    public required int ExitCode { get; init; }

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
