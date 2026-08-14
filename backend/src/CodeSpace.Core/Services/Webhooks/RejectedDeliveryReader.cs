using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Webhooks;

public sealed class RejectedDeliveryReader : IRejectedDeliveryReader, IScopedDependency
{
    /// <summary>
    /// The ceiling on one answer. A provider that cannot reach us retries on a ladder, so an
    /// unreachable instance writes thousands of these in an afternoon and an uncapped read would
    /// hand the browser all of them. Fifty is what a person scrolls; the older ones are the same
    /// refusal repeated, and the count that matters ("this is still happening") is legible from
    /// the newest few.
    /// </summary>
    public const int MaxDeliveries = 50;

    private readonly CodeSpaceDbContext _db;

    public RejectedDeliveryReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<RepositoryRejectedDeliveries> ListForRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var teamId = await RequireOwningTeamAsync(repositoryId, cancellationToken).ConfigureAwait(false);

        var rows = await LoadRecentRefusalsAsync(teamId, repositoryId, cancellationToken).ConfigureAwait(false);

        return new RepositoryRejectedDeliveries { Deliveries = rows.Select(ToDelivery).ToList(), Cap = MaxDeliveries };
    }

    /// <summary>
    /// The team is taken from the repository rather than from the request's team header. The header
    /// is right for a caller who came through the authorization pipeline and absent for one who
    /// bypassed it (the Admin role, which background jobs hold), and a reader that answered
    /// differently for the two would be a reader nothing could safely reuse.
    /// </summary>
    private async Task<Guid> RequireOwningTeamAsync(Guid repositoryId, CancellationToken cancellationToken) =>
        await _db.Repository.AsNoTracking()
            .Where(r => r.Id == repositoryId)
            .Select(r => (Guid?)r.TeamId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Repository {repositoryId} not found");

    /// <summary>
    /// Newest first, capped, and scoped by team AND repository.
    ///
    /// <para>The <c>repository_id IS NULL</c> arm is what makes the team filter load-bearing rather
    /// than belt-and-braces: an unattributed refusal is only ever attributable to a team, so without
    /// it this query would hand one team's discarded deliveries to another. It is in the answer at
    /// all because a delivery nobody could place is still a delivery that arrived and was thrown
    /// away — the exact thing being looked for — and the tab says which rows those are.</para>
    ///
    /// <para>Entities rather than a projection because the reason has to be split off the front of
    /// <c>error</c>, which SQL would express as a pair of substring expressions nobody could read.
    /// The cap bounds it to fifty rows whose payload columns are the auditor's empty <c>{}</c>.</para>
    /// </summary>
    private async Task<IReadOnlyList<WorkflowRunRequest>> LoadRecentRefusalsAsync(Guid teamId, Guid repositoryId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunRequest.AsNoTracking()
            .Where(r => r.Status == WorkflowRunRequestStatus.Rejected
                        && r.TeamId == teamId
                        && (r.RepositoryId == repositoryId || r.RepositoryId == null))
            .OrderByDescending(r => r.ReceivedAt).ThenByDescending(r => r.Id)
            .Take(MaxDeliveries)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private static RejectedDelivery ToDelivery(WorkflowRunRequest row)
    {
        var (reason, detail) = SplitReason(row.Error);

        return new RejectedDelivery
        {
            Id = row.Id,
            ReceivedAt = row.ReceivedAt,
            RepositoryId = row.RepositoryId,
            Reason = reason,
            Detail = detail,
            ExternalEventId = row.ExternalEventId,
            RawHeadersRedactedJson = row.RawHeadersRedactedJson,
            VerificationResultJson = row.VerificationResultJson,
        };
    }

    /// <summary>
    /// The auditor writes <c>error</c> as <c>"{reason}: {detail}"</c>. Splitting on the FIRST
    /// separator hands the caller the reason on its own, which is what decides the sentence the
    /// operator reads — "no workflow was listening" and "the signature did not match" are not the
    /// same news and must not arrive in the same tone.
    ///
    /// <para>An error with no separator is handed back whole as the detail with no reason. Rather
    /// than guessing: a row we cannot classify is still evidence a delivery arrived and was
    /// discarded, and the reader has a sentence for the case where all it has is the raw error.</para>
    /// </summary>
    private static (string Reason, string Detail) SplitReason(string? error)
    {
        if (string.IsNullOrEmpty(error)) return ("", "");

        var separator = error.IndexOf(": ", StringComparison.Ordinal);

        return separator < 0 ? ("", error) : (error[..separator], error[(separator + 2)..]);
    }
}
