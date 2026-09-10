using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// P15: every model POST belongs to one logical launch's budget. <c>LlmBudgetGuard.GuardedAsync</c> throws this when
/// a call reaches it with no scope, or a scope whose <c>Budget</c> ledger was never wired — the fail-open this type
/// replaces silently spent past a launch's cap, unmetered and unlogged. This is a programming-error signal, not an
/// operator-facing failure: the remedy is code (thread the plane's ledger + cap through its <c>LlmCallScope</c>), or,
/// for a plane that legitimately has no launch (operator calibration, a benchmark cell's own cap), mark it
/// explicitly <c>Unbudgeted(reason)</c> so it passes through logged and counted rather than silently.
/// </summary>
public sealed class UnscopedModelCallException : Exception, IFailure
{
    public UnscopedModelCallException(string plane, string callSite)
        : base($"Model call on plane '{plane}' ({callSite}) reached the budget guard with no budget ledger wired and was not marked Unbudgeted. Thread the launch's scope (Budget + CapUsd), or mark this plane explicitly Unbudgeted(reason).")
    {
        Plane = plane;
        CallSite = callSite;
    }

    /// <summary>The scope's <c>Kind</c> (e.g. "supervisor.decision", "agent.critic") — or "(unscoped)" when the call reached the guard with no scope at all.</summary>
    public string Plane { get; }

    /// <summary>The model being called, naming which physical request would have escaped metering.</summary>
    public string CallSite { get; }

    public FailureKind Kind => FailureKind.Internal;

    public string Code => FailureCodes.UnscopedModelCall;
}
