namespace CodeSpace.Messages.Agents;

/// <summary>
/// ONE repository's final reviewable integrated branch on a MULTI-repo supervisor run (resolver loop #379, S7-D1) —
/// the per-repo analogue of <c>SupervisorTurnResult.IntegratedBranch</c>, which is single-valued and so is null for a
/// multi-repo run (there is no single integrated branch — each repo integrates on its own axis). A pure data noun
/// (Rule 18.1): a projection of the latest clean multi-repo merge's per-repo integration block, surfaced as the
/// <c>agent.supervisor</c> node's <c>repositoryBranches</c> output. EMPTY for a single-repo run (which surfaces the
/// single <c>integratedBranch</c> instead).
///
/// <para>NOTE — this is NOT a verbatim drop-in for <c>git.open_change_set</c> / <c>git.open_pr</c> (unlike agent.code's
/// <c>repositoryResults</c>): the head lives under <see cref="IntegratedBranch"/>, not the <c>producedBranch</c> /
/// <c>sourceBranch</c> key those nodes read, and no PR base is carried (the integration block has none). The branch is
/// an already-RECONCILED head (the supervisor's vocabulary, mirroring the single-repo <c>integratedBranch</c>), so a
/// downstream per-repo PR-open needs a key-mapping step (<see cref="IntegratedBranch"/> → sourceBranch) + a separately
/// chosen base. The intended near-term consumer is the S7-D2 per-repo resolution loop, not a verbatim git-node bind.</para>
/// </summary>
public sealed record SupervisorRepositoryBranch
{
    /// <summary>The repository this integrated branch belongs to — the per-repo PR-open key. Null only on a degraded block with no resolvable repository id.</summary>
    public Guid? RepositoryId { get; init; }

    /// <summary>The repo's alias within the workspace (the human-facing handle).</summary>
    public required string Alias { get; init; }

    /// <summary>The reconciled, reviewable branch this repo's parallel agent work integrated cleanly into. A per-repo PR-open targets this as its source head (mapped from this <c>integratedBranch</c> key, with a separately chosen base) — see the type remarks on why it isn't a verbatim git-node bind.</summary>
    public required string IntegratedBranch { get; init; }
}
