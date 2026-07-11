namespace CodeSpace.Messages.Agents;

/// <summary>
/// ONE repository's final reviewable branch on a MULTI-repo supervisor run (resolver loop #379, S7-D1/S7-E) — the
/// per-repo analogue of <c>SupervisorTurnResult.IntegratedBranch</c>, which is single-valued and so is null for a
/// multi-repo run (there is no single integrated branch — each repo integrates on its own axis). A pure data noun
/// (Rule 18.1): a projection of the latest clean multi-repo merge's per-repo integration block, surfaced as the
/// <c>agent.supervisor</c> node's <c>repositoryBranches</c> output. EMPTY for a single-repo run (which surfaces the
/// single <c>integratedBranch</c> instead).
///
/// <para>Shaped to bind VERBATIM into <c>git.open_change_set</c>'s <c>repositories</c> input — the SAME generic per-repo
/// PR-open node agent.run's <c>repositoryResults</c> feeds (so the "last mile" of the resolver loop is a workflow edge,
/// not a new node): the node reads each entry's head via <c>sourceBranch</c> and base via <c>targetBranch</c>.
/// <see cref="SourceBranch"/> is the reconciled / integrated head (the supervisor's vocabulary, but exposed under the
/// PR-source key the node reads); <see cref="TargetBranch"/> is the per-repo base to open the PR into (the ref the
/// repo was integrated onto). A downstream <c>git.open_change_set</c> binds this array directly.</para>
/// </summary>
public sealed record SupervisorRepositoryBranch
{
    /// <summary>The repository this branch belongs to — the per-repo PR-open key. Null only on a degraded block with no resolvable repository id.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>The repo's alias within the workspace (the human-facing handle).</summary>
    public required string Alias { get; init; }

    /// <summary>The reconciled, reviewable branch this repo's parallel agent work integrated cleanly into — the PR's SOURCE (head). Named <c>sourceBranch</c> so it binds verbatim into <c>git.open_change_set</c>, which reads the head under that key.</summary>
    public required string SourceBranch { get; init; }

    /// <summary>The base branch to open this repo's PR INTO (the ref the work was integrated onto) — the PR's TARGET. Named <c>targetBranch</c> so it binds verbatim into <c>git.open_change_set</c>; empty when the run recorded no resolvable base (the node then reports that repo Failed — head, no PR target).</summary>
    public string TargetBranch { get; init; } = "";
}
