using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Exceptions;

/// <summary>Cancel this observer's work without terminating a physical process that a successor may own.</summary>
public sealed class AgentRunOwnershipLostException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;
    public string Code => FailureCodes.AgentRunOwnershipLost;
    public AgentRunOwnershipLostException(Guid runId) : base($"Agent run {runId} is no longer owned by this worker; stop observation without terminating the execution.") { }
}
