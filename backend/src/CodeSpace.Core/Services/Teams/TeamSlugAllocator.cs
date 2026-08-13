using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Slugs;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Teams;

/// <summary>
/// Where a team slug is chosen, for every path that mints a team.
///
/// <para>Team is the one slugged table whose uniqueness is instance-wide: <c>idx_team_slug</c>
/// (0001_initial.sql) indexes the slug alone, where project / workflow / agent / skill each scope theirs
/// to <c>(team_id, slug)</c> and so only have to be free inside one tenant. That makes the two paths
/// that create a team — someone opening a workspace, and an invitation being accepted into a new
/// account with a personal one — bidders in the same namespace, able to take a slug the other is going
/// to need. Both come through here so the probe that makes that safe exists once and neither can skip
/// it.</para>
/// </summary>
public sealed class TeamSlugAllocator : IScopedDependency
{
    /// <summary>
    /// How migration 0008 named a personal workspace, and the shape a new account still gets. Held as a
    /// PREFIX and not merely as the bare word: a workspace named "personal deadbeef" derives
    /// personal-deadbeef, which is precisely the slug the next account whose id starts deadbeef needs —
    /// and that account cannot be created while a workspace holds it.
    /// </summary>
    private const string PersonalPrefix = "personal-";

    /// <summary>
    /// What a slug falls back to when the name cannot supply one, and what pushes a name back out of
    /// the personal namespace. Distinct from that namespace, and never a name a person typed.
    /// </summary>
    private const string TeamPrefix = "team-";

    /// <summary>
    /// Slugs that would collide with a URL the SPA already routes.
    ///
    /// <para>"personal" is deliberately absent: keeping a workspace out of that namespace is
    /// <see cref="IsPersonal"/>'s job, and it has to be, because a reserved word here only earns a
    /// dedup suffix — personal-2 is still inside the namespace this is trying to protect. Nothing can
    /// reach the deduper with "personal" as its base any more, so listing it would be a dead entry
    /// that reads like protection.</para>
    /// </summary>
    private static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>(StringComparer.Ordinal) { "admin", "new", "settings", "teams" };

    private readonly CodeSpaceDbContext _db;

    public TeamSlugAllocator(CodeSpaceDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// The slug for a workspace someone is opening. <paramref name="teamId"/> is the id the row will be
    /// saved under, and is only consulted when the name cannot produce a slug of its own.
    /// </summary>
    public async Task<string> ForWorkspaceAsync(string name, Guid teamId, CancellationToken cancellationToken) =>
        await AllocateAsync(WorkspaceBase(name, teamId), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The slug for the personal workspace an account is created with. Keeps 0008's shape, so a personal
    /// team made today and one backfilled then are indistinguishable afterwards.
    /// </summary>
    public async Task<string> ForPersonalAsync(Guid userId, CancellationToken cancellationToken) =>
        await AllocateAsync(PersonalPrefix + Short(userId), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// What the name contributes, before dedup — unless it contributes nothing, or something that is not
    /// the caller's to take.
    /// </summary>
    private static string WorkspaceBase(string name, Guid teamId)
    {
        var slugified = Slug.Slugify(name);

        // Slugify keeps ASCII only, so every all-CJK name derives to the empty string and fell to one
        // shared literal: 設計組, 工程團隊 and 行銷 came out team, team-2 and team-3, a URL that numbers
        // workspaces rather than naming one. When the name can say nothing, the id is what is left that
        // still tells two of them apart.
        if (slugified.Length == 0) return TeamPrefix + Short(teamId);

        if (!IsPersonal(slugified)) return slugified;

        // A name is not allowed to derive into the personal namespace, so it is moved out of it rather
        // than refused — the person asked for a workspace called something, not for an error about a
        // prefix they have no reason to know exists. Prefixing keeps what they typed in the URL.
        return Cap(TeamPrefix + slugified);
    }

    /// <summary>
    /// The bare word counts as inside the namespace: left to the dedup suffix it becomes personal-2,
    /// which is the very place reserving it was meant to keep a workspace out of.
    /// </summary>
    private static bool IsPersonal(string slug) => slug == "personal" || slug.StartsWith(PersonalPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The first free variant of <paramref name="baseSlug"/> across every live team, matching the
    /// <c>deleted_date IS NULL</c> the unique index is partial on.
    ///
    /// <para>Deduping rather than trusting the base is what both callers need, for different reasons:
    /// two teams called "Platform" is an ordinary thing to want, and a signup must not fail because
    /// eight hex characters of a fresh id happen to be spoken for. Probing costs one query; the
    /// alternative — letting the insert violate the index and catching it — would mean recovering a
    /// transaction Postgres has already aborted, with the new account and its memberships inside it.
    /// </para>
    ///
    /// <para>What this does NOT do is make concurrent creation safe. Probe-then-insert is
    /// time-of-check-to-time-of-use: two people opening "Platform" at the same moment both probe,
    /// both are told platform-2 is free, and the second SaveChanges violates the index. That surfaces
    /// as a failed request the person can retry, which is the right failure — the wrong one, and the
    /// one this exists to prevent, is a signup dying on a slug the account had no say in.</para>
    /// </summary>
    private async Task<string> AllocateAsync(string baseSlug, CancellationToken cancellationToken)
    {
        var taken = await _db.Team.AsNoTracking()
            .Where(t => t.DeletedDate == null && t.Slug.StartsWith(SlugDeduper.ProbePrefix(baseSlug)))
            .Select(t => t.Slug)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return SlugDeduper.DeriveAvailable(baseSlug, taken.ToHashSet(StringComparer.Ordinal), ReservedSlugs);
    }

    private static string Short(Guid id) => id.ToString("N")[..8];

    /// <summary>Prefixing can push a name-derived slug past the length every other slug is held to.</summary>
    private static string Cap(string slug) => slug.Length <= Slug.MaxLength ? slug : slug[..Slug.MaxLength].TrimEnd('-');
}
