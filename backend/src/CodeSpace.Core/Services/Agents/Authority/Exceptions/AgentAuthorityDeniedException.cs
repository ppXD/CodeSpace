using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Authority.Exceptions;

public sealed class AgentAuthorityDeniedException : Exception, IFailure
{
    public AgentAuthorityDeniedException(string reason) : base($"Execution authority denied ({reason}). Restore authorization or explicitly launch a new run; an unverifiable authored version must be republished by an authorized publisher; automatic retry cannot authorize this action.") => Reason = reason;
    public string Reason { get; }
    public FailureKind Kind => FailureKind.Forbidden;
    public string Code => FailureCodes.AgentAuthorityDenied;
}
