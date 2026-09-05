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
    /// <para><b>The unresolved-repository tier.</b> <see cref="PublishManifest.RepositoryId"/> is "the catalog
    /// repository, WHEN RESOLVED" — a pre-column row carries null, and so does a live run whose repository never
    /// resolved to a catalog id. Every manifest-backed consumer already honours such a row as this repository's
    /// evidence (<c>PublishManifestRepositorySelector</c>), so filtering on the concrete id alone reads that ledger as
    /// EMPTY and silently drops it to the caller's fallback — the very anchor this rule exists to replace.</para>
    ///
    /// <para><b>And what bounds it.</b> The merge and <c>git.integrate_run</c> lanes hand this rule the run-wide,
    /// ALL-repository ledger, so "no concrete id anywhere" does NOT mean "one repository": a multi-repository run over
    /// unresolved repositories writes an all-null ledger, and reading it whole would anchor repository X's integration
    /// on repository Y's root. The tier therefore fires only when the rooted rows resolve to ONE repository identity —
    /// a single distinct <see cref="PublishManifest.RepositoryAlias"/> (the per-workspace name that is never null and
    /// IS the row's idempotency key), none of them naming a DIFFERENT concrete repository. That is the selector's own
    /// "a concrete mismatch never inherits the compatibility fallback" bound, widened by exactly what the alias adds:
    /// a null row beside its own repository's concrete row is still that repository's earlier evidence — the shape the
    /// staging lane's producer set carries when one producer resolved a catalog id and another did not.</para>
    /// </summary>
    public static string? OldestRecordedBase(IReadOnlyList<PublishManifest> manifests, Guid repositoryId)
    {
        var rooted = manifests.Where(m => m.Kind == PublishManifestKind.Agent && !string.IsNullOrWhiteSpace(m.BaseSha)).ToList();

        return NamesOneRepositoryAndItIsThisOne(rooted, repositoryId) ? Oldest(rooted) : Oldest(rooted.Where(m => m.RepositoryId == repositoryId));
    }

    /// <summary>Whether a null-id row in this ledger is THIS repository's own evidence: every rooted row sits under one workspace alias and none of them names a different repository. False on an empty set — nothing recorded a base, so the caller's fallback stands.</summary>
    private static bool NamesOneRepositoryAndItIsThisOne(IReadOnlyList<PublishManifest> rooted, Guid repositoryId) =>
        rooted.All(m => m.RepositoryId is null || m.RepositoryId == repositoryId)
        && rooted.Select(m => m.RepositoryAlias).Distinct(StringComparer.Ordinal).Count() == 1;

    private static string? Oldest(IEnumerable<PublishManifest> rows) =>
        rows.OrderBy(m => m.CreatedDate).ThenBy(m => m.Id).FirstOrDefault()?.BaseSha;
}
