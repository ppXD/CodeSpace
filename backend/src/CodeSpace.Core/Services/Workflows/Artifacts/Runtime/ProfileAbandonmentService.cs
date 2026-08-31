using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Storage;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public sealed class ProfileAbandonmentService : IProfileAbandonmentService
{
    private const int MaxBatchSize = 200;

    /// <summary>
    /// How much of one batch may come back with the SAME problem code before the pass stops and names it.
    ///
    /// <para>Sits next to the batch size because it only means anything against it. Protects against a
    /// destination-wide fault — an unmounted volume, a credential that lost its permission, a namespace that no
    /// longer resolves — being read as a statement about each object underneath: unrelated objects fail for
    /// unrelated reasons, so one answer repeated across a quarter of a batch is the destination talking. Every
    /// remaining placement would only be asked a question already answered wrongly, and the operator would be handed
    /// a pass that closed nothing without saying why.</para>
    /// </summary>
    private const double UniformAnswerFraction = 0.25;

    /// <summary>Below this many identical answers there is no population to generalize from, whatever the fraction of a small batch works out to.</summary>
    private const int MinimumUniformAnswers = 5;

    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactCasPurgeCoordinator _purge;

    public ProfileAbandonmentService(CodeSpaceDbContext db, IArtifactCasPurgeCoordinator purge)
    {
        _db = db;
        _purge = purge;
    }

    public async Task<ProfileAbandonmentSummary> AbandonAsync(Guid teamId, Guid actorId, Guid profileId, int batchSize, CancellationToken cancellationToken)
    {
        var revisions = await RevisionIdsAsync(teamId, profileId, cancellationToken).ConfigureAwait(false);
        var batch = await UnreleasedAsync(teamId, revisions, Math.Clamp(batchSize, 1, MaxBatchSize), cancellationToken).ConfigureAwait(false);
        var answers = new List<ArtifactCasAbandonResult?>(batch.Count);
        ArtifactCasProblemCode? stoppedBy = null;

        foreach (var placement in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            answers.Add(await AbandonOneAsync(teamId, actorId, placement, cancellationToken).ConfigureAwait(false));
            stoppedBy = UniformAnswer(answers, batch.Count);

            if (stoppedBy != null) break;
        }

        return Summarize(Outcomes(batch, answers), stoppedBy, await CountUnreleasedAsync(teamId, revisions, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The one answer a single shared fault gives to every object under the profile, or null while the batch still
    /// disagrees.
    ///
    /// <para>Weighs only answers one fault could explain arriving for every row — see
    /// <see cref="OneFaultCouldExplain"/>.</para>
    /// </summary>
    private static ArtifactCasProblemCode? UniformAnswer(List<ArtifactCasAbandonResult?> answers, int batchSize)
    {
        var ceiling = Math.Max(MinimumUniformAnswers, (int)Math.Ceiling(batchSize * UniformAnswerFraction));

        return answers.OfType<ArtifactCasAbandonResult.Rejected>().Select(rejected => rejected.Problem.Code)
            .Where(OneFaultCouldExplain).GroupBy(code => code).Where(code => code.Count() >= ceiling)
            .Select(code => (ArtifactCasProblemCode?)code.Key).FirstOrDefault();
    }

    /// <summary>
    /// Whether ONE fault could explain this refusal arriving for every row, which is what makes agreement across a
    /// batch mean anything.
    ///
    /// <para><c>StaleWorker</c> is the exclusion, and the only one. It is produced by drains racing each other rather
    /// than by any fault: the claim was taken and then lost, by the same race that makes
    /// <see cref="AbandonOneAsync"/> report an unclaimable placement as no answer at all. Two passes over one profile
    /// agree on it for every row they overlap on, and reading that agreement as a broken destination would stop a
    /// pass on evidence nothing produced but the pass itself.</para>
    ///
    /// <para>Not every refusal kept here reached the destination, and that is deliberate: a broker timeout and an
    /// unavailable credential broker are both decided in this process, before a request leaves it. They stay because
    /// one fault does explain them across every row, and a pass that can open nothing is exactly as stuck as one the
    /// destination refuses. What is excluded is agreement produced by the rows racing each other.</para>
    /// </summary>
    private static bool OneFaultCouldExplain(ArtifactCasProblemCode code) => code != ArtifactCasProblemCode.StaleWorker;

    private static ProfileAbandonmentSummary Summarize(List<ProfilePlacementOutcome> outcomes, ArtifactCasProblemCode? stoppedBy, int remaining) => new()
    {
        Examined = outcomes.Count,
        Abandoned = Count(outcomes, ProfilePlacementAbandonOutcomeValue.Abandoned),
        StillServed = Count(outcomes, ProfilePlacementAbandonOutcomeValue.StillServed),
        Unanswered = Count(outcomes, ProfilePlacementAbandonOutcomeValue.Unanswered),
        Remaining = remaining,
        StoppedBy = stoppedBy?.ToString(),
        Outcomes = outcomes,
    };

    /// <summary>
    /// One entry per placement the pass reached, paired with the answer it got. The answers are appended in batch
    /// order, so a pass the breaker stopped simply has fewer of them than the batch it was handed.
    /// </summary>
    private static List<ProfilePlacementOutcome> Outcomes(List<Placement> batch, List<ArtifactCasAbandonResult?> answers) =>
        batch.Zip(answers, Outcome).ToList();

    private static int Count(List<ProfilePlacementOutcome> outcomes, ProfilePlacementAbandonOutcomeValue outcome) =>
        outcomes.Count(candidate => candidate.Outcome == outcome);

    private static ProfilePlacementOutcome Outcome(Placement placement, ArtifactCasAbandonResult? answer) => new()
    {
        LocationId = placement.LocationId,
        ObjectKey = placement.ObjectKey,
        Outcome = OutcomeOf(answer),
        Detail = DetailOf(answer),
    };

    private static ProfilePlacementAbandonOutcomeValue OutcomeOf(ArtifactCasAbandonResult? answer) => answer switch
    {
        ArtifactCasAbandonResult.Abandoned => ProfilePlacementAbandonOutcomeValue.Abandoned,
        ArtifactCasAbandonResult.StillServed => ProfilePlacementAbandonOutcomeValue.StillServed,
        _ => ProfilePlacementAbandonOutcomeValue.Unanswered,
    };

    /// <summary>
    /// What the destination said, in its own words where it gave any — never this service's summary of them.
    ///
    /// <para>Absent means ONE thing: no destination was asked, because the row was already settled while this pass
    /// walked it. That is another drain racing this one, and it is answered by waiting rather than by repairing
    /// anything — see <see cref="AbandonOneAsync"/> for why no other shape reaches here.</para>
    /// </summary>
    private static string? DetailOf(ArtifactCasAbandonResult? answer) => answer switch
    {
        ArtifactCasAbandonResult.Abandoned abandoned => abandoned.Evidence,
        ArtifactCasAbandonResult.StillServed served => served.Evidence,
        ArtifactCasAbandonResult.Rejected rejected => rejected.Problem.Code.ToString(),
        _ => null,
    };

    /// <summary>
    /// Claims one placement and asks the destination about it, or null when the claim could not be taken.
    ///
    /// <para>A claim that cannot be taken is not a failure of the pass: the placement is being worked on by something
    /// else, or has already been settled. Either way the honest count is "no answer", and the next call sees it. It
    /// is also not an ANSWER — the race decided it, not any fault — so it is kept apart from the refusals the circuit
    /// breaker may generalize from: see <see cref="OneFaultCouldExplain"/>.</para>
    ///
    /// <para>Claiming can also REFUSE, in four ways, and none of them is worth a carrier of its own — but for two
    /// different reasons, and the distinction is what a future reader needs. The claim refuses an unclaimable STATE,
    /// and that arm is unreachable from here: every state <see cref="Unreleased"/> selects is one the claim admits,
    /// and the rows it would refuse instead (<c>Pending</c>, <c>Failed</c>) are permitted by the schema but written
    /// by no production path. The other three — the object holds no placement, the named placement is gone, its
    /// profile revision is gone — ARE reachable, because this pass selects and then claims, and a sibling pass or
    /// the reaper can settle a row in between. They need no carrier because they are the same fact as the race
    /// above: the row moved under us, nothing was learned about the destination, and the next call sees whatever it
    /// became.</para>
    ///
    /// <para>So a carrier becomes necessary the moment a refusal can mean something a later pass will NOT resolve
    /// on its own. Widening <see cref="Unreleased"/> to a state the claim rejects, or narrowing the claim, produces
    /// exactly that — and so would any new refusal arm that is neither "someone else got there first" nor a state
    /// this pass chose to select.</para>
    /// </summary>
    private async Task<ArtifactCasAbandonResult?> AbandonOneAsync(Guid teamId, Guid actorId, Placement placement, CancellationToken cancellationToken)
    {
        var claimed = await _purge.ClaimAsync(new ArtifactCasPurgeRequest
        {
            TeamId = teamId, ArtifactObjectId = placement.ArtifactObjectId, ActorId = actorId, ArtifactLocationId = placement.LocationId,
        }, cancellationToken).ConfigureAwait(false);

        return claimed is ArtifactCasPurgeClaimResult.Claimed claim
            ? await _purge.AbandonAsync(claim.Claim, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private async Task<List<Guid>> RevisionIdsAsync(Guid teamId, Guid profileId, CancellationToken cancellationToken) =>
        await _db.StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.TeamId == teamId && revision.StorageProfileId == profileId)
            .Select(revision => revision.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// One batch, least-recently-touched first, so a placement that refuses costs itself its turn and not the rest
    /// of the profile.
    ///
    /// <para>Claiming a placement stamps <c>LastModifiedDate</c> before the destination is asked anything, so every
    /// placement this pass examined sorts behind every placement it did not. Under a fixed order the circuit breaker
    /// stopped at the same rows on every pass: ceiling-many persistent refusers at the head meant nothing behind
    /// them was ever examined again and <c>Remaining</c> could not reach zero. Rotating costs nothing when the batch
    /// is healthy, because a placement that drains leaves this population for good.</para>
    /// </summary>
    private async Task<List<Placement>> UnreleasedAsync(Guid teamId, List<Guid> revisions, int take, CancellationToken cancellationToken) =>
        revisions.Count == 0 ? [] : await Unreleased(teamId, revisions)
            .OrderBy(location => location.LastModifiedDate).ThenBy(location => location.Id)
            .Take(take)
            .Select(location => new Placement(location.Id, location.ArtifactObjectId, location.ObjectKey))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<int> CountUnreleasedAsync(Guid teamId, List<Guid> revisions, CancellationToken cancellationToken) =>
        revisions.Count == 0 ? 0 : await Unreleased(teamId, revisions).CountAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The same population the retirement guard counts, so a caller draining to zero is draining to what actually unblocks them.</summary>
    private IQueryable<ArtifactLocation> Unreleased(Guid teamId, List<Guid> revisions) =>
        _db.ArtifactLocation.AsNoTracking().Where(location => location.TeamId == teamId
            && revisions.Contains(location.StorageProfileRevisionId)
            && location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted);

    private sealed record Placement(Guid LocationId, Guid ArtifactObjectId, string ObjectKey);
}
