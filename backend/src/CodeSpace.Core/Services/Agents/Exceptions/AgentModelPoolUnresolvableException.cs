using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Exceptions;

/// <summary>
/// An agent run bound to the operator's allowed model pool found nothing in it that can run — every pooled model is
/// disabled, or its credential is no longer active — so the run is refused rather than put on a model outside the pool.
/// Its code is the run's exit reason, and a respawn meets the same pool, so the node treats it as deterministic.
/// </summary>
public sealed class AgentModelPoolUnresolvableException : Exception, IFailure
{
    public AgentModelPoolUnresolvableException(string message) : base(message) { }

    public FailureKind Kind => FailureKind.Unprocessable;
    public string Code => FailureCodes.AgentModelPoolUnresolvable;
}
