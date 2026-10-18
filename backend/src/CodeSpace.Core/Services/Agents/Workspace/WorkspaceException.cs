using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Thrown when a workspace can't be prepared — a failed clone, a missing ref, or git being unavailable.
/// The executor maps it to a clean agent-run failure (the run never starts the harness).
///
/// <para>Not sealed so a failure with a known cause can say so (<see cref="GitCredentialHelperUnsupportedException"/>) while
/// every catch site that handles this type keeps handling it.</para>
/// </summary>
public class WorkspaceException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Unprocessable;

    public string Code => FailureCodes.WorkspaceUnresolvable;


    public WorkspaceException(string message) : base(message) { }

    public WorkspaceException(string message, Exception inner) : base(message, inner) { }
}
