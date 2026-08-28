using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The mutual exclusion every path that BOOTSTRAPS a team's storage must take: the one that decides, for a team that
/// has no route for a data class yet, that it is now getting one.
///
/// <para>Two such paths exist and neither can see the other's decision before it commits — the shipped
/// <c>AgentRunLogStorageReadiness</c> missing-only bootstrap, and the deployment-default materializer. Both read "this
/// team has no route", both build a profile, and only one of the two route inserts can win
/// <c>ux_storage_route_team_data_class</c>. The loser has already committed a profile and a credential by then, and
/// neither can ever be deleted: <c>storage_profile</c> and <c>storage_credential</c> both reject DELETE, and a route
/// can never return to Draft. Serialising the decision is what keeps that from happening.</para>
///
/// <para>This is a METHOD rather than an exposed constant on purpose. "Take the same lock" has to be structural: two
/// callers that each compose their own <c>pg_advisory_xact_lock</c> call are one typo — a different seed, a Guid
/// formatted differently — away from taking two different locks and believing they are excluded. Sharing the seed is
/// only sufficient if the hashed text is identical too, so both belong in one place.</para>
///
/// <para>The seed follows this codebase's existing convention: the number of the migration that introduced the
/// mechanism the lock protects. It is pinned by test because changing it silently un-excludes every other caller
/// rather than failing to compile.</para>
/// </summary>
public static class StorageBootstrapLock
{
    /// <summary>The <c>hashtextextended</c> seed. 117 is migration 0117, which introduced Agent Run log storage — the first bootstrap to need this lock.</summary>
    internal const int Seed = 117;

    /// <summary>
    /// Takes the per-team bootstrap lock for the CURRENT transaction. Blocks rather than failing, which is what a
    /// bootstrap wants: the second caller waits and then observes the first caller's committed route, instead of
    /// racing it to a unique-index violation it can only recover from by rolling back work it cannot delete.
    /// </summary>
    public static Task TakeAsync(DatabaseFacade database, Guid teamId, CancellationToken cancellationToken) =>
        database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({teamId.ToString()}, {Seed}))", cancellationToken);
}
