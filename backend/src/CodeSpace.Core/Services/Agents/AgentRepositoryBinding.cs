using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The calling run's hold on a repository its tool call names (<see cref="AgentRunPosture.Repositories"/>). Team scope
/// alone let an agent reach every repository of its team at any ref it chose; the run's own binding is narrower. A
/// repository outside it gets the "not found" a missing or foreign one gets, so a refusal never confirms that a guessed
/// id exists. A writable repository is the run's own to work in, at any ref. Read-only context is something the run
/// reads: a command checks out only its bound branch or its default branch, and an agent writes nothing to it — no pull
/// request opened, merged, reviewed or commented on. Pure — consulted by <c>NodeAgentTool</c> for every
/// manifest-declared repository input and by <c>RunCommandService</c> for an agent's command.
///
/// <para>The ref pin holds what a command CHECKS OUT, so the working tree an agent builds and runs from read-only
/// context is the content the operator bound, never a branch it named. It does not hide what the provider publishes
/// about a bound repository: its pull requests — their list, diffs and checks — are readable through the git read tools
/// like the rest of the repository, whatever ref it is bound at. A repository whose open pull requests must stay out of
/// a run's reach is one not to bind to it.</para>
/// </summary>
public static class AgentRepositoryBinding
{
    /// <summary>
    /// The refusal for a repository outside the run's binding — the same answer whether the id is this team's, another
    /// team's or nobody's, and byte-identical to <c>RunCommandService</c>'s own tenant-filter miss.
    /// </summary>
    public static string NotFound(Guid repositoryId) => $"Repository {repositoryId} not found.";

    /// <summary>The refusal for a pull-request write to a repository the run is bound to only as read-only context.</summary>
    public static string ReadOnlyContextWrite(Guid repositoryId) =>
        $"Repository {repositoryId} is bound to this run as read-only context, so an agent may not open, merge, review or comment on its pull requests.";

    /// <summary>The refusal for a command checking out a ref of read-only context outside its binding.</summary>
    public static string RefOutsideBinding(Guid repositoryId, WorkspaceRepositorySpec bound, string? requestedRef, string defaultBranch) =>
        $"Repository {repositoryId} is bound to this run as read-only context at '{bound.Ref ?? defaultBranch}', so a command may check out only that branch or the default branch '{defaultBranch}', not '{requestedRef}'.";

    /// <summary>The run's binding of <paramref name="repositoryId"/>, or null when the run is not bound to it.</summary>
    public static WorkspaceRepositorySpec? Find(AgentRunPosture caller, Guid repositoryId) =>
        caller.Repositories.FirstOrDefault(repository => repository.RepositoryId == repositoryId);

    /// <summary>Whether an agent may write to a bound repository through its provider: only a writable one. An access this code does not know is held like read-only context.</summary>
    public static bool AllowsWrite(WorkspaceRepositorySpec bound) => bound.Access == WorkspaceAccess.Write;

    /// <summary>
    /// Whether a command may check out <paramref name="requestedRef"/> of a bound repository: any ref of a writable one;
    /// of read-only context, its bound branch (else the default branch) or the default branch — compared exactly, as git
    /// names refs. A blank ref is the default branch, as the clone reads it. An access this code does not know is held
    /// like read-only context.
    /// </summary>
    public static bool AllowsRef(WorkspaceRepositorySpec bound, string? requestedRef, string defaultBranch)
    {
        if (AllowsWrite(bound) || string.IsNullOrWhiteSpace(requestedRef)) return true;

        return requestedRef == defaultBranch || requestedRef == (bound.Ref ?? defaultBranch);
    }
}
