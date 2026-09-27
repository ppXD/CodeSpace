using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Sandbox.Exceptions;

/// <summary>
/// A run whose model is brokered and whose network is its own — off, or narrowed to an allowlist — REFUSED because this
/// host would confine it but has no way to carry it to that broker. Such a child reaches its broker only through the
/// <c>codespace-mcp relay</c> inside its sandbox and the lease's per-run socket; without either, the agent reaches no
/// model and the run burns its whole timeout and the CLI's retries on failures that read like a provider outage.
/// Refusing names the wall instead, and does it before anything is spent.
///
/// <para>Unavailable, like <see cref="EgressSubnetReservationUnavailableException"/>: nothing about the launch can be
/// changed to make it work, and the identical launch succeeds untouched on a worker that has the helper and can open
/// the socket. <see cref="Cause"/> says which was missing, in the same words the message uses. The type and its code
/// keep the name of the namespace seal the relay replaced, because the supervisor and stored results key on it.</para>
/// </summary>
public sealed class SealedEgressUnavailableException : Exception, IFailure
{
    /// <summary>The <c>codespace-mcp</c> helper, which runs the relay inside the sandbox, is not where the worker looks for it, or is there and cannot run the relay: a build from before it, a self-contained publish, or a file that does not start.</summary>
    public const string CauseRelayMissing = "no codespace-mcp helper on this worker can relay the CLI's broker address";

    /// <summary>The run's model broker served its lease without the per-run socket the relay connects to.</summary>
    public const string CauseBrokerSocketUnavailable = "the run's model broker could not open the socket the relay connects to";

    private const string Remedy = "Install this release's codespace-mcp next to the worker's assembly, as a framework-dependent build or a single-file publish (or point CODESPACE_MCP_PROXY_PATH at one), or fix what stopped the broker's socket from binding (its Warning names the path); a retry lands on another worker, which may have both.";

    public SealedEgressUnavailableException(string cause)
        : base($"This run's network is its own and its model is reached through its broker, which on this worker means a relay inside the sandbox and a per-run socket, but {cause}. Refusing to launch an agent that could not reach its model. {Remedy}")
    {
        Cause = cause;
    }

    /// <summary>Which wall the host hit — one of the <c>Cause*</c> constants, optionally followed by what was looked for.</summary>
    public string Cause { get; }

    FailureKind IFailure.Kind => FailureKind.Unavailable;
    string IFailure.Code => FailureCodes.SandboxSealedEgressUnavailable;
    string? IFailure.ClientMessage => "This host cannot give this run's sandbox a route to its model.";
}
