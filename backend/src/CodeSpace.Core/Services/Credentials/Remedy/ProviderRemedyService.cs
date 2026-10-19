using CodeSpace.Core.Authorization;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Failures;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials.Remedy;

/// <summary>
/// Builds a <see cref="ProviderRemedy"/>: reads the failure, reads what it names as it is now, reads who is asking, and states what
/// is wrong and what they can do — one flat pipeline, each step its own method, with the words and the permission rules kept in
/// the pure <see cref="RemedyCopy"/>, <see cref="RemedyActions"/> and <see cref="RemedyResolution"/> so they can be tested without a database.
///
/// <para>Read-only. Nothing here changes a credential, a repository or a run: every action the remedy offers is a request the
/// client makes of the endpoint that already does it, under that endpoint's own authorization.</para>
/// </summary>
public sealed partial class ProviderRemedyService : IProviderRemedyService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly TeamMembershipResolver _membership;
    private readonly IRunFailureReader _failures;

    private RemedyContext _ctx = default!;

    public ProviderRemedyService(CodeSpaceDbContext db, ICurrentUser currentUser, TeamMembershipResolver membership, IRunFailureReader failures)
    {
        _db = db;
        _currentUser = currentUser;
        _membership = membership;
        _failures = failures;
    }

    public async Task<ProviderRemedy?> GetForRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var recorded = await _failures.ReadAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        if (recorded is null) return null;

        _ctx = new RemedyContext { TeamId = teamId, Code = recorded.Failure.Code, Failure = recorded.Failure, At = recorded.At, ScopeRepositoryIds = recorded.ScopeRepositoryIds, OffersRerun = true };

        return await BuildAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProviderRemedy?> GetForRefusedDeliveriesAsync(Guid repositoryId, IReadOnlyList<RejectedDelivery> deliveries, Guid teamId, CancellationToken cancellationToken)
    {
        var newest = NewestRefusalBlamingTheCredential(deliveries, repositoryId);

        if (newest is null) return null;

        var failure = new PersistedFailure { Code = newest.Reason, RepositoryId = repositoryId, Details = await ReadLookupAsync(newest.Id, teamId, cancellationToken).ConfigureAwait(false) };

        _ctx = new RemedyContext { TeamId = teamId, Code = newest.Reason, Failure = failure, At = newest.ReceivedAt, ScopeRepositoryIds = Array.Empty<Guid>(), OffersRerun = false };

        return await BuildAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>What the refusal row kept of why the lookup failed (<see cref="StandingLookupCauses"/>, the status the provider answered). Null for a refusal blamed on the credential outright, and for a row written before it was kept.</summary>
    private async Task<IReadOnlyDictionary<string, string>?> ReadLookupAsync(Guid requestId, Guid teamId, CancellationToken cancellationToken)
    {
        var metadata = await _db.WorkflowRunRequest.AsNoTracking().Where(r => r.Id == requestId && r.TeamId == teamId).Select(r => r.RequestMetadataJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        return PersistedFailureJson.Read(metadata)?.Details;
    }

    /// <summary>The newest refusal that blames the repository's credential. A delivery that could not be placed on any repository says nothing about this one's.</summary>
    private static RejectedDelivery? NewestRefusalBlamingTheCredential(IReadOnlyList<RejectedDelivery> deliveries, Guid repositoryId) =>
        deliveries
            .Where(d => d.RepositoryId == repositoryId && d.Reason is WorkflowRunRequestRejectionReasons.CredentialRejected or WorkflowRunRequestRejectionReasons.AuthorStandingUnknown)
            .OrderByDescending(d => d.ReceivedAt)
            .FirstOrDefault();

    private async Task<ProviderRemedy> BuildAsync(CancellationToken cancellationToken)
    {
        await LoadRepositoriesAsync(cancellationToken).ConfigureAwait(false);
        await LoadCredentialsAsync(cancellationToken).ConfigureAwait(false);
        ChooseCredential();
        await LoadOwnerNameAsync(cancellationToken).ConfigureAwait(false);
        await LoadProviderInstanceAsync(cancellationToken).ConfigureAwait(false);
        await LoadConnectedCredentialsAsync(cancellationToken).ConfigureAwait(false);
        await ResolveCallerAsync(cancellationToken).ConfigureAwait(false);
        await LoadPersonAsync(cancellationToken).ConfigureAwait(false);

        DecideResolved();
        StateFacts();
        ChooseActions();

        return await AssembleAsync(cancellationToken).ConfigureAwait(false);
    }
}
