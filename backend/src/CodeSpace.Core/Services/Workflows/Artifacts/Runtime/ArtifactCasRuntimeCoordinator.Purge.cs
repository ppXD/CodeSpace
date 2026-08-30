using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

public sealed partial class ArtifactCasRuntimeCoordinator
{
    public async Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken)
    {
        var claim = await ClaimAsync(request, cancellationToken).ConfigureAwait(false);
        return claim switch
        {
            // Corrupt is claimable so it can be abandoned, but this entry point exists to delete bytes. Handing the
            // claim on would strand the row in Deleting when the delete refused it, which is worse than the refusal.
            ArtifactCasPurgeClaimResult.Claimed corrupt when corrupt.Claim.ClaimedFrom == ArtifactLocationState.Corrupt
                => await ReleaseAndRejectAsync(corrupt.Claim, cancellationToken).ConfigureAwait(false),
            ArtifactCasPurgeClaimResult.Claimed claimed => await DeleteAsync(claimed.Claim, cancellationToken).ConfigureAwait(false),
            ArtifactCasPurgeClaimResult.Purged purged => new ArtifactCasPurgeResult.Purged(purged.LocationId, purged.LocationRevision, true),
            ArtifactCasPurgeClaimResult.Rejected rejected => new ArtifactCasPurgeResult.Rejected(rejected.Problem),
            _ => new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.ProviderFailure)),
        };
    }

    private async Task<ArtifactCasPurgeResult> ReleaseAndRejectAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        // Discarded deliberately: the refusal is the answer whatever the release did. NoEvidence cannot arise here —
        // this arm only runs for a claim taken from Corrupt, which is a state the row can be put straight back into.
        await ReleaseAsync(claim, ArtifactCasReleaseEvidence.Untouched, cancellationToken).ConfigureAwait(false);

        return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.LocationUnavailable));
    }

    public async Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken)
    {
        var timeout = Validate(request);
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var locations = await db.ArtifactLocation.FromSqlInterpolated($"""
            SELECT artifact_location.*, artifact_location.xmin
            FROM artifact_location
            WHERE team_id = {request.TeamId} AND artifact_object_id = {request.ArtifactObjectId}
            ORDER BY id
            FOR UPDATE
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (locations.Count == 0) return ClaimRejected(ArtifactCasProblemCode.ArtifactMissing);

        // The FOR UPDATE above stays object-wide on purpose — it is the mutual exclusion two concurrent drains of the
        // same object need. Only the SELECTION narrows: a caller draining one destination says which row, and a
        // caller of a single-placed object still says nothing.
        var location = request.ArtifactLocationId is { } named
            ? locations.SingleOrDefault(value => value.Id == named)
            : locations.Count == 1 ? locations[0] : null;

        if (location == null) return ClaimRejected(ArtifactCasProblemCode.ArtifactMissing);
        if (location.State == ArtifactLocationState.Purged)
            return new ArtifactCasPurgeClaimResult.Purged(location.Id, location.Revision);
        // Missing joins Available and Deleting: the destination has said the object is not there, and letting the row
        // reach Deleting is the only way it can reach Purged — which is what spends the idempotency generation and
        // makes that content writable under this revision again. Refusing it left the row unreachable by every path
        // at once: unreadable, undrainable, and blocking its profile's retirement forever.
        //
        // Corrupt is deliberately NOT admitted. It asserts the destination holds something that is NOT this object,
        // and the delete cannot always be conditioned — a provider without a stable ETag (the local driver, since the
        // recorded one is derived from a modification time) would delete whatever is at that key. A record positively
        // identified as naming someone else's bytes is closed by abandoning the record, never by deleting them.
        // Corrupt is claimable but not deletable: claiming is about taking the row, deleting is about touching bytes.
        // Without a claim it could never reach Deleting, therefore never Purged, therefore never release its profile
        // or let that content be written under this revision again — a record with no exit at all.
        if (location.State is not (ArtifactLocationState.Available or ArtifactLocationState.Deleting
            or ArtifactLocationState.Missing or ArtifactLocationState.Corrupt))
            return ClaimRejected(ArtifactCasProblemCode.LocationUnavailable);

        var claimedFrom = location.State;
        var profile = await db.StorageProfileRevision.AsNoTracking()
            .Where(value => value.TeamId == request.TeamId && value.Id == location.StorageProfileRevisionId)
            .Select(value => new { value.StorageProfileId, value.Revision })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (profile == null) return ClaimRejected(ArtifactCasProblemCode.ProfileRevisionMissing);

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        location.State = ArtifactLocationState.Deleting;
        location.Revision++;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.LastModifiedDate = now;
        location.LastModifiedBy = request.ActorId;
        db.ArtifactLocationEvent.Add(PurgeEvent(location, request.ActorId, now));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new ArtifactCasPurgeClaimResult.Claimed(new ArtifactCasPurgeClaim
        {
            ClaimedFrom = claimedFrom,
            TeamId = request.TeamId, ArtifactObjectId = request.ArtifactObjectId,
            LocationId = location.Id, LocationRevision = location.Revision,
            StorageProfileId = profile.StorageProfileId, StorageProfileRevision = profile.Revision,
            ObjectKey = location.ObjectKey, ProviderETag = location.ProviderETag,
            ProviderObjectVersion = location.ProviderObjectVersion, ActorId = request.ActorId, OperationTimeout = timeout,
        });
    }

    public async Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        Validate(claim);
        if (claim.ClaimedFrom == ArtifactLocationState.Corrupt)
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.LocationUnavailable));
        if (!await ClaimIsCurrentAsync(claim, cancellationToken).ConfigureAwait(false))
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true));
        var activation = await OpenDriverAsync(new DriverActivationRequest(claim.TeamId, claim.StorageProfileId,
            claim.StorageProfileRevision, StorageProfileEligibility.Read, claim.OperationTimeout, StorageProviderCapabilities.Delete), cancellationToken).ConfigureAwait(false);
        if (activation.Problem != null) return new ArtifactCasPurgeResult.Rejected(activation.Problem);

        StorageRuntimeDriverLease? lease = activation.Lease!;
        try
        {
            var deletion = await InvokeAsync(token => lease.Driver.DeleteAsync(new ArtifactStorageDeleteRequest(claim.ObjectKey)
            {
                ExpectedETag = DurableETag(claim.ProviderETag, lease.Driver.Capabilities), ExpectedVersion = claim.ProviderObjectVersion,
            }, token), claim.OperationTimeout, cancellationToken, lease).ConfigureAwait(false);
            if (deletion.Problem != null) return new ArtifactCasPurgeResult.Rejected(deletion.Problem, EffectMayHaveOccurred: true);
            if (deletion.Timeout) return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.ProviderTimeout, true), EffectMayHaveOccurred: true);
            if (deletion.Value?.Error is { Code: not ArtifactStorageErrorCode.Missing } error)
                return new ArtifactCasPurgeResult.Rejected(Map(error, readMissing: true));

            return await FinalizePurgeAsync(claim, ArtifactLocationClosureDetails.Deleted(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (lease != null) await DisposeLeaseQuietlyAsync(lease).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Settles a claimed placement as <c>Purged</c> without deleting anything, once a live HEAD proves the
    /// destination cannot serve the object.
    ///
    /// <para>Proof is taken here, under this claim, with Read eligibility so a Disabled or Retired profile can still
    /// be asked. It is never inherited from a stored health row: that describes a destination at some past moment and
    /// against a different eligibility, and the one thing this must never do is close the record of bytes somebody
    /// can still read.</para>
    ///
    /// <para>Every answer that is ABOUT the destination or the credential — the object is not there, the bucket is
    /// not there, the key is refused — is grounds to close the record, but only once the destination has answered
    /// for ITSELF as well: see <see cref="WeighAsync"/>. An answer that serves the object is not grounds at all, and
    /// releases the claim instead. An answer that is merely a bad moment is neither, and leaves the claim standing
    /// for a caller to retry or release.</para>
    ///
    /// <para>The activation refusal below closes NOTHING, and needs no corroboration because it never had an answer
    /// to corroborate. Every refusal that can arrive there was decided inside this worker before a request left it:
    /// the broker reads the profile snapshot from our own database, finds the provider factory in this process's own
    /// registry, decrypts the credential with this process's own key, and constructs the driver locally. Code by
    /// code, for every refusal <c>OpenDriverAsync</c> can produce:</para>
    /// <list type="bullet">
    /// <item><c>ProfileMissing</c>, <c>ProfileRevisionMissing</c> — no. Our own records lost the config; the
    /// destination that config named is untouched, and it is no longer even known which one it was.</item>
    /// <item><c>ProfileNotActive</c> — no. Our own governance state, and this call asks with Read eligibility
    /// precisely so a Disabled or Retired profile still opens.</item>
    /// <item><c>ProfileInvalid</c>, <c>Unsupported</c> — no. A stored config THIS build cannot parse, or a factory
    /// that rejected it; a rolled-back image gives them for a destination that was serving a minute ago.</item>
    /// <item><c>ProviderUnavailable</c> — no, and most sharply. The provider module is absent from THIS worker's
    /// image. Read as evidence, one worker deployed without it closes every record placed through the profile.</item>
    /// <item><c>CredentialUnavailable</c>, <c>CredentialInvalid</c> — NEVER, for the reason
    /// <see cref="ReportsItselfGone"/> refuses the same answer from a probe: a key this worker could not obtain or
    /// decrypt says nothing about whether the bytes exist, and they sit intact behind a permission somebody can
    /// grant back.</item>
    /// <item><c>ProviderTimeout</c>, <c>ProviderUnavailableTransient</c>, <c>CredentialBrokerUnavailable</c>,
    /// <c>ProviderFailure</c> — no, and never were. Answers about the moment.</item>
    /// </list>
    /// <para>So the claim is handed back and the refusal reported retryable, exactly as an uncorroborated HEAD is:
    /// nothing about the placement was established, and the next pass has to ask again. The code is kept, so an
    /// operator can still be told WHY the pass got nowhere.</para>
    /// </summary>
    public async Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        Validate(claim);
        if (!await ClaimIsCurrentAsync(claim, cancellationToken).ConfigureAwait(false))
            return new ArtifactCasAbandonResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true));

        var activation = await OpenDriverAsync(new DriverActivationRequest(claim.TeamId, claim.StorageProfileId,
            claim.StorageProfileRevision, StorageProfileEligibility.Read, claim.OperationTimeout, StorageProviderCapabilities.None), cancellationToken).ConfigureAwait(false);

        if (activation.Problem is { } refusal) return await ReleaseUnansweredAsync(claim, refusal, cancellationToken).ConfigureAwait(false);

        StorageRuntimeDriverLease? lease = activation.Lease!;
        try
        {
            var head = await InvokeAsync(token => lease.Driver.HeadAsync(new ArtifactStorageHeadRequest(claim.ObjectKey), token),
                claim.OperationTimeout, cancellationToken, lease).ConfigureAwait(false);

            if (head.Problem != null) return new ArtifactCasAbandonResult.Rejected(head.Problem);
            if (head.Timeout) return new ArtifactCasAbandonResult.Rejected(Problem(ArtifactCasProblemCode.ProviderTimeout, true));

            if (head.Value?.Error is { } error)
                return await WeighAsync(claim, lease, error, cancellationToken).ConfigureAwait(false);

            // A successful HEAD proves something is AT the key, not that the key holds THIS object. For a placement
            // already recorded Corrupt that distinction is the whole question: the destination is healthy and serving
            // something, and treating presence as service released the claim, while the delete path refuses Corrupt
            // outright — leaving the record with no exit at all and its profile permanently un-retirable.
            if (await ServesSomethingElseAsync(claim, head.Value!.Metadata!, cancellationToken).ConfigureAwait(false))
                return await FinalizeAbandonAsync(claim, ArtifactLocationAbandonment.HoldsSomethingElse(claim.ObjectKey), cancellationToken).ConfigureAwait(false);

            // Discarded deliberately: "the destination served it" is what this call answers, and it is true whether or
            // not the row could be handed back. A release that did not take leaves the marker for the next drain pass.
            await ReleaseAsync(claim, ArtifactCasReleaseEvidence.Served, cancellationToken).ConfigureAwait(false);

            return new ArtifactCasAbandonResult.StillServed(claim.LocationId, $"the destination served {claim.ObjectKey}");
        }
        finally
        {
            if (lease != null) await DisposeLeaseQuietlyAsync(lease).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Turns one per-object answer into an outcome, after asking the destination whether it can still speak at all.
    ///
    /// <para>A provider cannot tell a deleted object from a namespace it can no longer see: the local driver resolves
    /// both with <c>File.Exists</c>, and a credential that lost its permission refuses every key alike. So an answer
    /// that <see cref="Settles(ArtifactStorageError)"/> is evidence about THIS object only once the destination has
    /// answered for itself — the corroboration <c>ArtifactLocationVerifier</c> takes before it demotes a placement to
    /// <c>Missing</c>, against the same trap, and widened by <see cref="AnswersForItself"/> only far enough that a
    /// destination reporting ITSELF gone still counts as having answered. Without it an unmounted volume closes every
    /// record under its profile in one pass, and closing a record is not undoable: the checksum, the size, the ETag
    /// and the provider version are nulled, and nothing left can say what those bytes were.</para>
    ///
    /// <para>Uncorroborated, the claim is handed back rather than kept. Nothing was established that was not known
    /// before it, so the row belongs exactly where it was, for the next pass to ask again.</para>
    /// </summary>
    private async Task<ArtifactCasAbandonResult> WeighAsync(ArtifactCasPurgeClaim claim, StorageRuntimeDriverLease lease, ArtifactStorageError error, CancellationToken cancellationToken)
    {
        // Probed ONLY for an answer that would otherwise close the record: a destination having a bad moment must not
        // also be asked to prove it, and every extra call lands on one that is already struggling.
        var corroborated = Settles(error) && await DestinationAnswersAsync(claim, lease, cancellationToken).ConfigureAwait(false);

        return Weigh(error, corroborated) switch
        {
            AbandonmentEvidence.Conclusive => await FinalizeAbandonAsync(claim, ArtifactLocationAbandonment.Unservable(error.Code, claim.ObjectKey), cancellationToken).ConfigureAwait(false),
            AbandonmentEvidence.Uncorroborated => await ReleaseUnansweredAsync(claim, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true), cancellationToken).ConfigureAwait(false),
            _ => new ArtifactCasAbandonResult.Rejected(Map(error, readMissing: false)),
        };
    }

    /// <summary>
    /// What one per-object answer is worth, once the destination has been asked whether it still answers for itself.
    ///
    /// <para>Kept as a total function over the two inputs so the middle case cannot be lost in a refactor: an answer
    /// that settles and an answer that settles WITH corroboration are different answers, and only the second one may
    /// close a record.</para>
    /// </summary>
    internal static AbandonmentEvidence Weigh(ArtifactStorageError error, bool destinationAnswers) => !Settles(error)
        ? AbandonmentEvidence.Inconclusive
        : destinationAnswers ? AbandonmentEvidence.Conclusive : AbandonmentEvidence.Uncorroborated;

    /// <summary>What one per-object answer proves about the placement it names.</summary>
    public enum AbandonmentEvidence
    {
        /// <summary>The destination answered for itself AND could not serve this object. The only outcome that may close a record.</summary>
        Conclusive,

        /// <summary>An answer that would close the record, from a destination that could not answer for itself — which is what a vanished namespace and a revoked credential both look like from here.</summary>
        Uncorroborated,

        /// <summary>An answer about the moment or about the request, which was never grounds to close anything.</summary>
        Inconclusive,
    }

    /// <summary>
    /// Asks whether the destination is still answering for ITSELF, never for the object.
    ///
    /// <para>Never <c>Initialize</c>: a probe that provisions what is missing manufactures its own corroboration,
    /// which is exactly how a vanished mount came to testify that every object beneath it had been deleted.</para>
    /// </summary>
    private async Task<bool> DestinationAnswersAsync(ArtifactCasPurgeClaim claim, StorageRuntimeDriverLease lease, CancellationToken cancellationToken)
    {
        var probe = await InvokeAsync(token => lease.Driver.ProbeAsync(new ArtifactStorageProbeRequest(), token),
            claim.OperationTimeout, cancellationToken, lease).ConfigureAwait(false);

        return AnswersForItself(probe.Value);
    }

    /// <summary>
    /// Whether a probe is the destination ANSWERING for itself. Answering and being healthy are not the same thing.
    ///
    /// <para>A destination that is reachable answers — <c>Available</c>, and <c>ReadOnly</c>, which is a read that
    /// succeeded and a write that was refused, a refusal this never needs. So does one that says IT IS GONE: the
    /// deleted bucket this whole operation exists for reports <c>NoSuchBucket</c> about itself as readily as about
    /// the key, and demanding a healthy probe instead made that exit unreachable at precisely the destination that
    /// needs it. That second arm is <see cref="ReportsItselfGone"/>, and nothing wider.</para>
    ///
    /// <para>Every other refusal corroborates nothing — a credential that lost its permission, a namespace out of
    /// reach for the moment — and so does a probe that produced no result at all. Nothing is closed on any of them,
    /// which is the safe direction for a step whose effect cannot be undone.</para>
    /// </summary>
    internal static bool AnswersForItself(ArtifactStorageProbeResult? probe) => probe != null
        && (probe.Status is ArtifactStorageProbeStatus.Available or ArtifactStorageProbeStatus.ReadOnly
            || (probe.Error is { } refusal && ReportsItselfGone(refusal)));

    /// <summary>
    /// Whether a probe's own refusal is the destination saying THAT IT ITSELF IS GONE — the only refusal that may
    /// corroborate a per-object answer.
    ///
    /// <para>Deliberately not <see cref="Settles(ArtifactStorageError)"/>, and never to be derived from it. That
    /// predicate answers a PER-OBJECT question, where <c>Forbidden</c> means "you may not read THIS key" and is a
    /// durable fact about the key. Asked at DESTINATION granularity the identical code means "your credential lost
    /// its permission", which says nothing whatever about whether the objects exist — it is indistinguishable from a
    /// namespace you can no longer see, which is the case this corroboration was built for. The two questions differ,
    /// and sharing one helper is what made them look like one.</para>
    ///
    /// <para>Code by code, as an answer a probe gives about the destination itself:</para>
    /// <list type="bullet">
    /// <item><c>Unavailable</c> non-retryable — YES, and only this. It is where the classifier puts a namespace that
    /// is gone for good: <c>NoSuchBucket</c> is carved out of the otherwise-retryable code precisely because
    /// retrying does not bring a deleted bucket back.</item>
    /// <item><c>Unavailable</c> retryable — no. A 5xx, a network fault, an unmounted volume; the local driver
    /// answers exactly this for a root that is not there, because mounting it back is a thing that happens.</item>
    /// <item><c>Unauthorized</c>, <c>Forbidden</c> — NEVER. A rotated or de-scoped key refuses every key alike and
    /// reveals nothing about what is behind it. Read as corroboration it closes every record under the profile while
    /// the bytes sit intact behind a permission somebody can grant back.</item>
    /// <item><c>Missing</c> — no. It is the contract's word for an OBJECT that is not there, and a probe names no
    /// object; a driver answering it about the destination has said something the contract cannot attribute.</item>
    /// <item><c>Throttled</c>, <c>ProviderFailure</c> — no. Answers about the moment, which is what retrying is
    /// for.</item>
    /// <item><c>InvalidRequest</c>, <c>Unsupported</c>, <c>AlreadyExists</c>, <c>ConditionNotMet</c>,
    /// <c>IntegrityMismatch</c>, <c>Corrupt</c> — no. Answers about a REQUEST or about content, neither of which a
    /// probe makes or carries.</item>
    /// </list>
    /// </summary>
    internal static bool ReportsItselfGone(ArtifactStorageError error) =>
        error.Code == ArtifactStorageErrorCode.Unavailable && !error.IsRetryable;

    /// <summary>
    /// Hands the claim back and refuses, for a pass that got no answer it may act on — a settling per-object answer
    /// the destination could not corroborate, or a destination this worker could not open at all.
    ///
    /// <para>The release outcome is discarded deliberately: the refusal is the answer whatever the release did.
    /// <c>Untouched</c> is the honest evidence — neither caller established anything that was not known before the
    /// claim — so the row goes back exactly where the claim found it. A claim taken from a <c>Deleting</c> orphan has
    /// no such place and stays the marker it already was, which is also exactly as it was.</para>
    ///
    /// <para>Retryable whatever the caller's code says: the placement is exactly as it was, so the next pass has to
    /// ask again. A refusal that reads durable here would tell an operator to stop asking about a row nothing has
    /// answered for yet.</para>
    /// </summary>
    private async Task<ArtifactCasAbandonResult> ReleaseUnansweredAsync(ArtifactCasPurgeClaim claim, ArtifactCasProblem refusal, CancellationToken cancellationToken)
    {
        await ReleaseAsync(claim, ArtifactCasReleaseEvidence.Untouched, cancellationToken).ConfigureAwait(false);

        return new ArtifactCasAbandonResult.Rejected(refusal with { IsRetryable = true });
    }

    /// <summary>
    /// Which provider answers say something durable about the destination rather than about this moment.
    ///
    /// <para><c>Missing</c>, <c>Unauthorized</c> and <c>Forbidden</c> are statements about the object or the
    /// credential. <c>Unavailable</c> is two different answers wearing one code — a deleted bucket AND a transient
    /// 5xx or network fault both classify to it — and retryability is what tells them apart: the classifier marks a
    /// gone namespace non-retryable because retrying does not bring a deleted bucket back. A retryable Unavailable is
    /// a bad moment, and a bad moment must never close the record of bytes it could not testify about.</para>
    /// </summary>
    /// <summary>
    /// Whether the destination is demonstrably holding something OTHER than this object.
    ///
    /// <para>Compared against the CAS object's own identity — its size and digest — rather than against the
    /// placement row, because the row is what is under suspicion. Only content-derived evidence counts: size always
    /// is, and a provider-computed hash is when the provider returns one. <c>ProviderETag</c> is deliberately not
    /// compared; it is provider-defined and a local destination derives it from a modification time, so a restore or
    /// a migration would read as a different object.</para>
    ///
    /// <para>Absence of proof is not proof: a provider that returns no hash and a matching size yields false, and
    /// the claim is released. Closing a record needs a positive disagreement.</para>
    /// </summary>
    private async Task<bool> ServesSomethingElseAsync(ArtifactCasPurgeClaim claim, ArtifactStorageObjectMetadata metadata, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var identity = await db.ArtifactObject.AsNoTracking()
            .Where(value => value.TeamId == claim.TeamId && value.Id == claim.ArtifactObjectId)
            .Select(value => new { value.SizeBytes, value.Digest })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (identity == null) return false;
        if (metadata.Length != identity.SizeBytes) return true;

        return metadata.Sha256 is { } observed && !string.Equals(observed, Convert.ToHexStringLower(identity.Digest), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Settles(ArtifactStorageError error) => error.Code
        is ArtifactStorageErrorCode.Missing
        or ArtifactStorageErrorCode.Unauthorized
        or ArtifactStorageErrorCode.Forbidden
        || (error.Code == ArtifactStorageErrorCode.Unavailable && !error.IsRetryable);

    private async Task<ArtifactCasAbandonResult> FinalizeAbandonAsync(ArtifactCasPurgeClaim claim, ArtifactLocationAbandonment abandonment, CancellationToken cancellationToken)
    {
        var finalized = await FinalizePurgeAsync(claim, ArtifactLocationClosureDetails.Abandoned(abandonment), cancellationToken).ConfigureAwait(false);

        return finalized switch
        {
            ArtifactCasPurgeResult.Purged purged => new ArtifactCasAbandonResult.Abandoned(purged.LocationId, purged.LocationRevision, abandonment.Observed),
            ArtifactCasPurgeResult.Rejected rejected => new ArtifactCasAbandonResult.Rejected(rejected.Problem),
            _ => new ArtifactCasAbandonResult.Rejected(Problem(ArtifactCasProblemCode.ProviderFailure)),
        };
    }

    public async Task<ArtifactCasReleaseOutcome> ReleaseAsync(ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken)
    {
        Validate(claim);
        await using var db = CreateDb();
        var location = await db.ArtifactLocation.SingleOrDefaultAsync(value => value.TeamId == claim.TeamId
            && value.Id == claim.LocationId && value.ArtifactObjectId == claim.ArtifactObjectId
            && value.ObjectKey == claim.ObjectKey && value.ProviderETag == claim.ProviderETag
            && value.ProviderObjectVersion == claim.ProviderObjectVersion, cancellationToken).ConfigureAwait(false);
        if (location == null || location.State != ArtifactLocationState.Deleting || location.Revision != claim.LocationRevision) return ArtifactCasReleaseOutcome.Raced;

        var resting = await RestingStateAsync(db, claim, evidence, cancellationToken).ConfigureAwait(false);
        if (resting is not { } restored) return ArtifactCasReleaseOutcome.NoEvidence;

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        location.State = restored;
        location.Revision++;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.LastModifiedDate = now;
        location.LastModifiedBy = claim.ActorId;
        db.ArtifactLocationEvent.Add(PurgeEvent(location, claim.ActorId, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return ArtifactCasReleaseOutcome.Released;
        }
        catch (DbUpdateConcurrencyException) { return ArtifactCasReleaseOutcome.Raced; }
    }

    /// <summary>
    /// Where the placement goes once the claim is gone, or null when nothing can be established and the row is left
    /// exactly as it is.
    ///
    /// <para>Without evidence the answer is where the claim found it, and no better: a row claimed from Missing or
    /// Corrupt was not good before, and declaring it good on the strength of a claim that touched nothing would put
    /// unreadable bytes back in front of every reader. That answer runs out when the claim was taken from an orphan
    /// a crashed worker left behind, because <c>Deleting</c> is the claim marker itself — writing it back releases
    /// the row into a state it can never leave.</para>
    ///
    /// <para>A HEAD that served the object is the one evidence that can restore a state the row LEFT, and it reads
    /// that state from the history rather than inventing one: the newest revision that is not the marker. The exit
    /// for an orphan is therefore the abandon path, which always holds a fresh HEAD.</para>
    /// </summary>
    private async Task<ArtifactLocationState?> RestingStateAsync(CodeSpaceDbContext db, ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken)
    {
        if (evidence != ArtifactCasReleaseEvidence.Served)
            return claim.ClaimedFrom == ArtifactLocationState.Deleting ? null : claim.ClaimedFrom;

        var history = db.ArtifactLocationEvent.AsNoTracking()
            .Where(entry => entry.TeamId == claim.TeamId && entry.ArtifactLocationId == claim.LocationId);

        return await RestingStates(history).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The states a placement has actually rested in, newest first. Every <c>Deleting</c> event is a claim marker rather than a state.</summary>
    internal static IQueryable<ArtifactLocationState?> RestingStates(IQueryable<ArtifactLocationEvent> history) =>
        history.Where(entry => entry.State != ArtifactLocationState.Deleting)
            .OrderByDescending(entry => entry.Revision).Select(entry => (ArtifactLocationState?)entry.State);

    private async Task<bool> ClaimIsCurrentAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var profileRevisionId = await db.StorageProfileRevision.AsNoTracking()
            .Where(value => value.TeamId == claim.TeamId && value.StorageProfileId == claim.StorageProfileId
                && value.Revision == claim.StorageProfileRevision)
            .Select(value => (Guid?)value.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return profileRevisionId != null && await db.ArtifactLocation.AsNoTracking().AnyAsync(value => value.TeamId == claim.TeamId
            && value.Id == claim.LocationId && value.ArtifactObjectId == claim.ArtifactObjectId
            && value.StorageProfileRevisionId == profileRevisionId && value.ObjectKey == claim.ObjectKey
            && value.ProviderETag == claim.ProviderETag && value.ProviderObjectVersion == claim.ProviderObjectVersion
            && value.State == ArtifactLocationState.Deleting && value.Revision == claim.LocationRevision, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the placement, recording in <paramref name="closureDetails"/> WHICH closure this was — the one thing
    /// the two callers do not otherwise leave behind, since both land on <c>Purged</c> with the same event.
    /// </summary>
    private async Task<ArtifactCasPurgeResult> FinalizePurgeAsync(ArtifactCasPurgeClaim claim, string closureDetails, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var location = await db.ArtifactLocation.SingleOrDefaultAsync(value => value.TeamId == claim.TeamId
            && value.Id == claim.LocationId && value.ArtifactObjectId == claim.ArtifactObjectId
            && value.ObjectKey == claim.ObjectKey && value.ProviderETag == claim.ProviderETag
            && value.ProviderObjectVersion == claim.ProviderObjectVersion, cancellationToken).ConfigureAwait(false);
        if (location == null) return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.ArtifactMissing), EffectMayHaveOccurred: true);
        if (location.State == ArtifactLocationState.Purged)
            return new ArtifactCasPurgeResult.Purged(location.Id, location.Revision, true);
        if (location.State != ArtifactLocationState.Deleting || location.Revision != claim.LocationRevision)
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true), EffectMayHaveOccurred: true);

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        location.State = ArtifactLocationState.Purged;
        location.Revision++;
        location.ProviderObjectVersion = null;
        location.ProviderETag = null;
        location.ProviderChecksumAlgorithm = null;
        location.ProviderChecksum = null;
        location.ObservedSizeBytes = null;
        location.VerifiedAt = null;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.LastModifiedDate = now;
        location.LastModifiedBy = claim.ActorId;
        db.ArtifactLocationEvent.Add(PurgeEvent(location, claim.ActorId, now, closureDetails));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new ArtifactCasPurgeResult.Purged(location.Id, location.Revision, false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new ArtifactCasPurgeResult.Rejected(Problem(ArtifactCasProblemCode.StaleWorker, true), EffectMayHaveOccurred: true);
        }
    }

    /// <summary>A claim marker or a release: the state moved, and neither of them closed anything there is a verb for.</summary>
    private static ArtifactLocationEvent PurgeEvent(ArtifactLocation location, Guid actorId, DateTimeOffset now) => PurgeEvent(location, actorId, now, "{}");

    private static ArtifactLocationEvent PurgeEvent(ArtifactLocation location, Guid actorId, DateTimeOffset now, string detailsJson) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
        EventType = ArtifactLocationEventType.StateChanged, State = location.State, ObservedAt = now,
        ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
        ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage, DetailsJson = detailsJson, CreatedBy = actorId,
    };

    private static TimeSpan Validate(ArtifactCasPurgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty || request.ArtifactObjectId == Guid.Empty || request.ActorId == Guid.Empty)
            throw new ArgumentException("Team, artifact object and actor ids are required.", nameof(request));
        return ValidateTimeout(request.OperationTimeout);
    }

    private static void Validate(ArtifactCasPurgeClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        if (claim.TeamId == Guid.Empty || claim.ArtifactObjectId == Guid.Empty || claim.LocationId == Guid.Empty
            || claim.LocationRevision <= 0 || claim.StorageProfileId == Guid.Empty || claim.StorageProfileRevision <= 0
            || string.IsNullOrWhiteSpace(claim.ObjectKey) || claim.ActorId == Guid.Empty)
            throw new ArgumentException("A purge claim requires exact team, object, location, revision, profile, key and actor coordinates.", nameof(claim));
        ValidateTimeout(claim.OperationTimeout);
    }

    private static ArtifactCasPurgeClaimResult ClaimRejected(ArtifactCasProblemCode code) => new ArtifactCasPurgeClaimResult.Rejected(Problem(code));
}
