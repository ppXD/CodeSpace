using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Exceptions;

/// <summary>
/// A physical execution WAS admitted for this attempt, and the acknowledgement that was supposed to make it reachable
/// did not land. Thrown by the launch acknowledgement seam so the run keeps the one status its facts support: the
/// process may be alive and it owns the workspace, so this observer must stop WITHOUT terminalizing the run and
/// WITHOUT reclaiming the clone it is running inside.
///
/// <para><b>Why it cannot be an ordinary failure.</b> Terminalizing here writes a verdict about work that is still
/// happening, and the terminal path then deletes the clone out from under a live agent. The recoverable facts are the
/// durable attempt row this launch is bound to and the receipt on its spool: the reconciler re-addresses the execution
/// by that exact attempt identity, and only when the receipt refuses that identity does the run fall back to the
/// ordinary abandon. Either way it needs the run left Running to be reached at all.</para>
///
/// <para>A refused re-address does NOT kill anything, and this deliberately does not promise that it will. The
/// refusal reasons are exactly the ones that make the process unattributable — a slot bound to another identity, a pid
/// minted on a foreign host or boot, a receipt that is unreadable — so a kill there would be aimed at a process this
/// run cannot prove is its own. The abandon's best-effort kill still runs for every run that HAS a durable handle.</para>
///
/// <para>It says nothing about whether the app-side handle committed. That is exactly the indeterminacy it exists to
/// carry — a committed-but-unacknowledged write and a write that never executed reach recovery the same way, through
/// the attempt identity rather than through a handle nobody can vouch for.</para>
/// </summary>
public sealed class AgentRunLaunchAdmittedException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;
    public string Code => FailureCodes.AgentRunLaunchAcknowledgementLost;

    public AgentRunLaunchAdmittedException(Guid runId, Guid attemptId, Exception inner)
        : base($"Agent run {runId} admitted a physical execution for attempt {attemptId} and could not acknowledge it; the run stays recoverable rather than terminal.", inner) { }
}
