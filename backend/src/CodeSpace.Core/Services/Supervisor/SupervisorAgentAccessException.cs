namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Raised when a supervisor-dispatched agent's effective persona (a model-authored slug OR the run-level profile
/// default) is not in the run's allowed agent (persona) pool — the per-agent persona privilege gate's fail-closed
/// signal, the persona analogue of <see cref="SupervisorModelAccessException"/>. The dispatch gate
/// (<c>RealSupervisorActionExecutor.ApplyDispatchAgentPool</c>) throws this; the turn service catches it and
/// terminalizes the spawn as a clean Failed (never a stranded Running).
/// </summary>
public sealed class SupervisorAgentAccessException : Exception
{
    public SupervisorAgentAccessException(string message) : base(message) { }
}
