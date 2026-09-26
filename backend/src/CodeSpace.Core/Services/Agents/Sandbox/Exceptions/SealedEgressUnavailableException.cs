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
/// sealed namespace needs — or, for a setup step that failed on a host that can seal (<see cref="SetupFailed"/>),
/// fixes what that step names. <see cref="Cause"/> says which it was, in the same words the message uses.</para>
/// </summary>
public sealed class SealedEgressUnavailableException : Exception, IFailure
{
    /// <summary>The worker lacks the <c>ip</c> or <c>nft</c> binary a sealed namespace is built with.</summary>
    public const string CauseMissingTools = "ip or nft is not installed on this worker";

    /// <summary>The binaries are there, but this process could not build a throwaway namespace.</summary>
    public const string CauseNoPrivilege = "this worker may not create a network namespace (it needs root with CAP_NET_ADMIN and CAP_SYS_ADMIN)";

    /// <summary>The run's model broker could only listen on loopback, which a sealed namespace cannot reach.</summary>
    public const string CauseBrokerLoopbackOnly = "the run's model broker could only listen on loopback, which a sealed namespace cannot reach";

    /// <summary>What an operator does about a worker that cannot build a sealed namespace at all.</summary>
    private const string GrantRemedy = "Grant the worker what a sealed namespace needs (see backend/Dockerfile.worker, EGRESS FILTERING); a retry on this host helps only once it can build one, which it re-checks at most once a minute.";

    /// <summary>What an operator does about one setup step that failed on a worker that can build a sealed namespace.</summary>
    private const string SetupRemedy = "This worker can build a sealed namespace, but a step of this one failed on its host: fix what that step names (a route or policy rule that discards the run's /30, or a namespace or veth left behind under the run's name). Every launch runs the setup afresh.";

    public SealedEgressUnavailableException(string cause) : this(cause, GrantRemedy) { }

    private SealedEgressUnavailableException(string cause, string remedy)
        : base($"This run's network is off and its model is reached through its broker; on this worker that is enforced with a network namespace sealed to that broker, but one cannot be built here: {cause}. Refusing to launch an agent that could not reach its model. {remedy}")
    {
        Cause = cause;
    }

    /// <summary>A sealed setup that failed at launch on a host that proved it can seal — a name collision, a route the kernel will not send the run's replies down — carrying the failed step's own error.</summary>
    public static SealedEgressUnavailableException SetupFailed(string? setupError) => new($"the sealed namespace's setup failed: {setupError}", SetupRemedy);

    /// <summary>Which wall the host hit — one of the <c>Cause*</c> constants, or a failed setup step's own error.</summary>
    public string Cause { get; }

    FailureKind IFailure.Kind => FailureKind.Unavailable;
    string IFailure.Code => FailureCodes.SandboxSealedEgressUnavailable;
    string? IFailure.ClientMessage => "This host cannot give a network-off run a sealed route to its model.";
}
