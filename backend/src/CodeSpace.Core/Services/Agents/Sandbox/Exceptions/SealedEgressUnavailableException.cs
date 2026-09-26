using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Sandbox.Exceptions;

/// <summary>
/// A network-off run whose model is brokered, REFUSED because this host would confine it but cannot seal its network
/// to that broker. Severing it instead — what a host that cannot seal would otherwise do — cuts the broker off along
/// with everything else, so the agent reaches no model and the run burns its whole timeout and the CLI's retries on
/// failures that read like a provider outage. Refusing names the wall instead, and does it before anything is spent.
///
/// <para>Unavailable, like <see cref="EgressSubnetReservationUnavailableException"/>: nothing about the launch can be
/// changed to make it work, and the identical launch succeeds untouched once an operator grants the worker what a
/// sealed namespace needs. <see cref="Cause"/> says which of those it was, in the same words the message uses.</para>
/// </summary>
public sealed class SealedEgressUnavailableException : Exception, IFailure
{
    /// <summary>The worker lacks the <c>ip</c> or <c>nft</c> binary a sealed namespace is built with.</summary>
    public const string CauseMissingTools = "ip or nft is not installed on this worker";

    /// <summary>The binaries are there, but this process could not build a throwaway namespace.</summary>
    public const string CauseNoPrivilege = "this worker may not create a network namespace (it needs root with CAP_NET_ADMIN and CAP_SYS_ADMIN)";

    /// <summary>The run's model broker could only listen on loopback, which a sealed namespace cannot reach.</summary>
    public const string CauseBrokerLoopbackOnly = "the run's model broker could only listen on loopback, which a sealed namespace cannot reach";

    public SealedEgressUnavailableException(string cause)
        : base($"This run's network is off and its model is reached through its broker; on this worker that is enforced with a network namespace sealed to that broker, but one cannot be built here: {cause}. " +
            "Refusing to launch an agent that could not reach its model. Grant the worker what a sealed namespace needs (see backend/Dockerfile.worker, EGRESS FILTERING); a retry on this host cannot help.")
    {
        Cause = cause;
    }

    /// <summary>Which wall the host hit — one of the <c>Cause*</c> constants, or a failed setup step's own error.</summary>
    public string Cause { get; }

    FailureKind IFailure.Kind => FailureKind.Unavailable;
    string IFailure.Code => FailureCodes.SandboxSealedEgressUnavailable;
    string? IFailure.ClientMessage => "This host cannot give a network-off run a sealed route to its model.";
}
