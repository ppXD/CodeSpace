using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Callbacks;

/// <summary>
/// The pending callback wait exists, but its row holds neither a ciphertext nor the token itself — only its digest, which
/// cannot be turned back into a URL. Every row this version or an earlier one writes keeps the token, so this needs a row
/// whose token column a later release blanked before sealing it, read after rolling back onto this version. The URL
/// already handed out still resumes the run. 409, because it is the row's state that refuses, not the caller.
/// </summary>
public sealed class WorkflowCallbackUrlUnrecoverableException : Exception, IFailure
{
    public WorkflowCallbackUrlUnrecoverableException(Guid runId, Guid waitId)
        : base($"The callback URL of wait {waitId} on run {runId} is no longer stored in a form that can be shown. The URL already handed out still resumes the run; to resume it by hand, re-issue the wait with a body.") { }

    public FailureKind Kind => FailureKind.Conflict;

    public string Code => FailureCodes.CallbackUrlUnrecoverable;
}
