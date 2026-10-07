namespace CodeSpace.Messages.Agents;

/// <summary>
/// One agent tool call's use of a repository its run is bound to, as the repository's own policy judges it before the
/// call runs: the run's team, the run's binding of the repository, the ref a command would check out of it, and whether
/// the call writes to it through its provider. Built by <c>NodeAgentTool</c> from the node's declared repository input.
/// </summary>
public sealed record AgentRepositoryUse
{
    /// <summary>The calling run's team — the only team the repository is resolved in.</summary>
    public required Guid TeamId { get; init; }

    /// <summary>The run's binding of the repository the call names: its id, access and bound ref.</summary>
    public required WorkspaceRepositorySpec Bound { get; init; }

    /// <summary>The ref a command would check out of the repository, as the node reads it — null for the default branch, or for a node that checks out nothing.</summary>
    public string? Ref { get; init; }

    /// <summary>True when the call writes to the repository through its provider — opens, merges, reviews or comments on a pull request.</summary>
    public bool Writes { get; init; }
}
