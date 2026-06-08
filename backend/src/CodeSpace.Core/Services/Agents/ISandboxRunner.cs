using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

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
