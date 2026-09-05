using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents.Workspace.Integrators;

/// <summary>
/// WHICH commit an integration checks out and anchors its base-integrity guard on — the ONE anchor rule all three
/// integrate lanes (the supervisor merge, <c>git.integrate_run</c>, and the dependency-staging handoff) resolve their
/// <see cref="IntegrationRequest.BaseSha"/> from.
///
/// <para><b>The rule: the ancestor-most base</b> — the one recorded base that is an ancestor of (or equal to) every
/// other base in the set. It is realized WITHOUT git as the OLDEST base the run's own publish ledger recorded for
/// that repository, because a run only ever re-parents FORWARD: dependency staging cuts a dependent from its
/// producer's head (or from a handoff branch that already integrated the producers), never the other way round. So
/// the first base a run recorded for a repository is upstream of every base it records after it.</para>
///
/// <para><b>Why the LEDGER and not the eligible contributions.</b> A producer can be withheld from the head (its own
/// grade rejected it) or excluded as unintegrable (it recorded a base but captured no work) — and then the first
/// ELIGIBLE contribution is a DEPENDENT whose base is the producer's head. Anchoring there refuses every sibling
/// still rooted at the repository base as a stale-base graft, conflicting the whole merge; and it checks out the
/// producer's head, so the producer's commits ride onto the reviewable branch while the applied count and the
/// per-contribution outcomes name only the dependent. The producer's manifest row survives BOTH exclusions, so the
/// ledger still names the run's root when the contribution list no longer can.</para>
///
/// <para><b>The same notion as the oracle base.</b> This is the reduction <c>SupervisorTurnService.ResolveOracleBaseShas</c>
/// already encodes (manifests newest-first, last write per repository wins ⇒ the oldest): the commit the integrated
/// candidate is rooted at and the commit its definition-of-done is graded against are ONE thing — the run's original
/// root for that repository. The launch-pin overlay that resolution adds on top is deliberately NOT applied here: a
/// pin is what the agents cloned at, so the ledger already carries it, while a pin AHEAD of a recorded base would
/// newly refuse real work.</para>
///
/// <para><b>It is a proxy, and it is checked.</b> Ancestry itself needs a clone, which no caller has;
/// <see cref="LocalGitBranchIntegrator"/> does, and its guard refuses any contribution whose base is neither this
/// commit nor a descendant of it. A wrong proxy therefore refuses honestly instead of grafting.</para>
/// </summary>
public static class IntegrationBaseAnchor
{
    /// <summary>The anchor every integrate lane calls: the run's recorded root for this repository, else the caller's own first-contribution base (the pre-ledger behaviour) when nothing recorded one. Non-null whenever <paramref name="firstContributionBase"/> is.</summary>
    public static string? Resolve(IReadOnlyList<PublishManifest> manifests, Guid repositoryId, string? firstContributionBase) =>
        OldestRecordedBase(manifests, repositoryId) ?? firstContributionBase;

    /// <summary>
    /// The run's original root for one repository — the OLDEST base its Agent-kind publish-manifest rows recorded,
    /// tie-broken on id so the pick is total and repeats across reads. Null when the ledger recorded no base for the
    /// repository, leaving the caller its own first-contribution fallback.
    ///
    /// <para><b>The legacy tier.</b> <see cref="PublishManifest.RepositoryId"/> post-dates the manifest table, so a
    /// row written before it carries null — and every manifest-backed consumer already honours that null row as the
    /// legacy single-repository carrier (<c>PublishManifestRepositorySelector</c>). Filtering on the concrete id
    /// alone therefore reads a legacy run's ledger as EMPTY and silently drops it to the caller's fallback, which is
    /// the very anchor this rule exists to replace. The tier fires only when NOTHING in the list carries a concrete
    /// repository — the same "a concrete mismatch never inherits the compatibility fallback" bound the selector
    /// draws, so one repository's root can never be handed to another's integration.</para>
    /// </summary>
    public static string? OldestRecordedBase(IReadOnlyList<PublishManifest> manifests, Guid repositoryId)
    {
        var rooted = manifests.Where(m => m.Kind == PublishManifestKind.Agent && !string.IsNullOrWhiteSpace(m.BaseSha)).ToList();

        if (Oldest(rooted.Where(m => m.RepositoryId == repositoryId)) is { } recorded) return recorded;

        return manifests.Any(m => m.RepositoryId is not null) ? null : Oldest(rooted.Where(m => m.RepositoryId is null));
    }

    private static string? Oldest(IEnumerable<PublishManifest> rows) =>
        rows.OrderBy(m => m.CreatedDate).ThenBy(m => m.Id).FirstOrDefault()?.BaseSha;
}
