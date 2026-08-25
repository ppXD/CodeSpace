namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Raised when a supervisor-dispatched agent's effective model is CREDENTIALED to the team but outside the operator's
/// allowed pool for this run — the per-agent model privilege gate's fail-closed signal, and deliberately NOT
/// re-authorable (a name the team credentials nowhere is the separate MODEL-MISS case, which the spawn executor rejects
/// re-authorably instead). <c>IModelPoolSelector.ResolveDispatchAsync</c> does not throw — it returns null, and the two
/// callers that raise this are the spawn executor's pre-staging screen and its post-resolution gate.
/// <para>The turn service catches it, records THAT DECISION Failed (so no <c>Running</c> row is stranded and a re-walk
/// cannot re-enter it), and then RE-THROWS — the node fails and the run terminalizes Failure. It is a clean DECISION
/// terminal, not a clean run terminal.</para>
/// </summary>
public sealed class SupervisorModelAccessException : Exception
{
    public SupervisorModelAccessException(string message) : base(message) { }
}
