namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// Raised when a supervisor-dispatched agent's effective model is not a credentialed model in the run's allowed pool —
/// the per-agent model privilege gate's fail-closed signal. The dispatch resolution (<c>IModelPoolSelector.ResolveDispatchAsync</c>)
/// throws this; the turn service catches it and terminalizes the spawn as a clean Failed (never a stranded Running).
/// </summary>
public sealed class SupervisorModelAccessException : Exception
{
    public SupervisorModelAccessException(string message) : base(message) { }
}
