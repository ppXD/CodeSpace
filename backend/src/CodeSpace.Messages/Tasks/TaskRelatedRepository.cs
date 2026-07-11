namespace CodeSpace.Messages.Tasks;

/// <summary>
/// One ADDITIONAL repository a task launch clones into the agent's workspace alongside the primary
/// (<c>RepositoryId</c>) — the operator-authored multi-repo input (Rule 18.1, a pure data noun). A narrow authoring
/// shape: only what the operator chooses (<see cref="RepositoryId"/> + an optional <see cref="Alias"/> /
/// <see cref="Access"/>); the derived fields (mount path, primary flag, clone ref) are NOT on the wire — the launch
/// service projects this onto a <c>WorkspaceRepositorySpec</c> through the SHARED <c>AgentWorkspaceAuthoring</c> the
/// agent.run node + the supervisor already funnel through (Rule 7 — ONE authored-repos → workspace projection).
/// <para>Null / empty list on the launch ⇒ a single-repo run (byte-identical to the pre-multi-repo launch).</para>
/// </summary>
public sealed record TaskRelatedRepository
{
    /// <summary>The bound repository to ALSO clone. Validated TEAM-SCOPED by the launch service (fail-closed — a foreign repo is a clear not-found, exactly like the primary).</summary>
    public required Guid RepositoryId { get; init; }

    /// <summary>The short name + mount folder for this repo (e.g. "api"). Blank ⇒ the workspace assigns a unique <c>repo-2</c>, <c>repo-3</c>, … .</summary>
    public string? Alias { get; init; }

    /// <summary>How the agent may use this repo: <c>"write"</c> = editable + branched, anything else (incl. null) ⇒ <c>"read"</c> context-only — the safe default.</summary>
    public string? Access { get; init; }
}
