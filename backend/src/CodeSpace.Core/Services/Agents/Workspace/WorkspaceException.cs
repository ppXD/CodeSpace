namespace CodeSpace.Core.Services.Agents.Workspace;

/// <summary>
/// Thrown when a workspace can't be prepared — a failed clone, a missing ref, or git being unavailable.
/// The executor maps it to a clean agent-run failure (the run never starts the harness).
/// </summary>
public sealed class WorkspaceException : Exception
{
    public WorkspaceException(string message) : base(message) { }

    public WorkspaceException(string message, Exception inner) : base(message, inner) { }
}
