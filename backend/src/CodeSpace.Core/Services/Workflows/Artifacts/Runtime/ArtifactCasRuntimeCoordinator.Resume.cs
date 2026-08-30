using System.Linq.Expressions;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Recovery for transfers whose worker never came back.
///
/// <para>This drives the saga with NO content stream, which is the one thing that separates it from a write. Everything
/// downstream of "are the bytes already there" is shared verbatim with <c>PutAsync</c> — the same fenced claim, the same
/// lease renewals, the same readback, the same commit — so a resumed transfer can never take a shortcut a writer is not
/// allowed, and no location row is ever written from here.</para>
/// </summary>
public sealed partial class ArtifactCasRuntimeCoordinator
{
    /// <summary>One resumed transfer's provider budget. The claim's lease is twice this, so a pass that is still working cannot be mistaken for a dead one.</summary>
    private static readonly TimeSpan ResumeOperationTimeout = TimeSpan.FromSeconds(60);

    public async Task<ArtifactTransferResumeSummary> ResumeAbandonedAsync(int batchSize, CancellationToken cancellationToken)
    {
        var abandoned = await AbandonedAsync(Math.Clamp(batchSize, 1, 500), cancellationToken).ConfigureAwait(false);
        var counts = new ResumeCounts();

        foreach (var candidate in abandoned)
        {
            cancellationToken.ThrowIfCancellationRequested();

            counts.Record(await ResumeOneAsync(candidate, cancellationToken).ConfigureAwait(false));
        }

        return counts.Summary(abandoned.Count);
    }

    /// <summary>
    /// The transfers whose worker is demonstrably gone, oldest abandonment first so a backlog drains in the order it
    /// accumulated. The cutoff is the DATABASE's clock, not this pod's: a worker's lease is written with
    /// <c>clock_timestamp()</c>, so comparing it against a drifted local clock is how a live worker gets declared dead.
    /// Served by <c>ix_artifact_transfer_intent_recovery</c> (0131), which was built for this sweep and had no reader.
    /// </summary>
    private async Task<IReadOnlyList<ResumeCandidate>> AbandonedAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);

        return await db.ArtifactTransferIntent.AsNoTracking().Where(Abandoned(now))
            .OrderBy(intent => intent.WorkerLeaseExpiresAt).ThenBy(intent => intent.Id)
            .Take(batchSize)
            .Select(intent => new ResumeCandidate(intent.TeamId, intent.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A transfer this sweep may take over: still running as far as the ledger knows, and holding a lease that has
    /// lapsed.
    ///
    /// <para>An EXPIRED lease is the only admissible evidence that a worker is gone, and it is why nothing here can
    /// interfere with one that is alive: a working transfer renews its lease across every provider call. A NULL lease
    /// is deliberately not abandonment — it is an intent nobody has claimed yet, whose caller is holding the bytes and
    /// is about to. That same clause is what keeps the ORDINARY <c>RetryScheduled</c> transfer out of reach, because
    /// scheduling a retry releases the lease: the caller that still has the content is the one entitled to make that
    /// attempt, and a resumer that took it would settle a write that was about to succeed.</para>
    ///
    /// <para><c>RetryScheduled</c> is nevertheless IN the state list, and deliberately. A worker claims a scheduled
    /// retry before it transitions the intent onward, so one that dies in that window leaves the row parked in
    /// <c>RetryScheduled</c> still HOLDING the lease it took — abandoned on exactly the evidence every other state is
    /// judged by, and reachable by nothing else. It still cannot jump the wait: <c>ClaimAsync</c> refuses a
    /// <c>RetryScheduled</c> intent whose <c>next_attempt_at</c> has not arrived.</para>
    ///
    /// <para>The state list mirrors the recovery index's own partial predicate so the index can serve this query.</para>
    /// </summary>
    internal static Expression<Func<ArtifactTransferIntent, bool>> Abandoned(DateTimeOffset now) => intent =>
        (intent.State == ArtifactTransferState.Intended || intent.State == ArtifactTransferState.Uploading
            || intent.State == ArtifactTransferState.Uploaded || intent.State == ArtifactTransferState.Verifying
            || intent.State == ArtifactTransferState.RetryScheduled)
        && intent.WorkerLeaseExpiresAt != null && intent.WorkerLeaseExpiresAt <= now;

    /// <summary>
    /// Takes over one abandoned transfer, claim FIRST and questions afterwards.
    ///
    /// <para>That ordering is the sweep's fairness, not an implementation detail. The batch is bounded and ordered by
    /// the very lease this claim advances, so a pass that refuses a transfer WITHOUT writing anything leaves it at the
    /// head of the next pass, and the one after that — and a single Disabled profile with a batch's worth of parked
    /// transfers behind it would then own every pass and starve every other intent in the deployment. Claiming first
    /// moves a transfer this pass cannot even ask about to the BACK of the queue instead, on the same lapsing lease
    /// that makes it claimable again later, and without settling anything.</para>
    /// </summary>
    private async Task<ResumeOutcome> ResumeOneAsync(ResumeCandidate candidate, CancellationToken cancellationToken)
    {
        var claimed = await ClaimAsync(candidate.TeamId, candidate.Id, SystemUsers.SeederId, ResumeOperationTimeout, cancellationToken).ConfigureAwait(false);

        // The fence this claim advanced is the entire authority to re-drive the transfer, and losing it means another
        // worker holds a live lease on it. Asking the destination anything on its behalf would put two processes on
        // one transfer, which is exactly what the fence exists to prevent — so a lost claim asks nothing, and the
        // worker that holds the lease is the one that already moved the row along.
        if (!claimed.Acquired) return ResumeOutcome.Contended;

        try
        {
            return await DriveResumeAsync(claimed.Intent, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _logger.LogWarning(exception, "Abandoned transfer {IntentId} could not be resumed this pass; its lease will lapse and a later pass will re-ask", candidate.Id);

            return ResumeOutcome.Inconclusive;
        }
    }

    private async Task<ResumeOutcome> DriveResumeAsync(IntentSnapshot claim, CancellationToken cancellationToken)
    {
        var destination = await ResumeDestinationAsync(claim, cancellationToken).ConfigureAwait(false);

        // A destination the team no longer admits placements to was never asked about the object, so it cannot have
        // answered for it. The claim above already moved this row behind everything the sweep has not tried yet, so
        // leaving it is a wait rather than a stall.
        if (destination.Problem != null) return Unresumable(claim.Id, destination.Problem);

        // Read is the only capability a resumer spends; the bytes it would have written are already at the destination.
        var activation = new DriverActivationRequest(claim.TeamId, destination.ProfileId, destination.Revision, StorageProfileEligibility.Write, ResumeOperationTimeout, StorageProviderCapabilities.StreamingRead);
        var create = await OpenDriverAsync(activation, cancellationToken).ConfigureAwait(false);

        // A destination that would not open never looked at the object key, so it cannot have answered for it either.
        if (create.Problem != null) return Unresumable(claim.Id, create.Problem);

        try
        {
            return await FinishAsync(claim, create.Lease!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeLeaseQuietlyAsync(create.Lease!).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Where the abandoned transfer was writing, admitted under the SAME write eligibility its caller had. A profile
    /// the team has since disabled or retired no longer accepts placements, and manufacturing one there would put a
    /// row into exactly the population the retirement gate reads to decide the destination is finished with.
    ///
    /// <para>Answered from the claim rather than from the selection row, and it makes no difference which:
    /// <c>storage_profile_revision_id</c> is immutable on the intent — 0131 rejects any update that moves it — so the
    /// fence changes nothing about the answer. What the fence buys is the ordering above, not a better answer.</para>
    /// </summary>
    private async Task<ResumeDestination> ResumeDestinationAsync(IntentSnapshot claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var revision = await db.StorageProfileRevision.AsNoTracking()
            .Where(value => value.TeamId == claim.TeamId && value.Id == claim.ProfileRevisionId)
            .Select(value => new { value.StorageProfileId, value.Revision })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (revision == null) return new ResumeDestination(Guid.Empty, 0, Problem(ArtifactCasProblemCode.ProfileRevisionMissing));

        var resolved = await ResolveProfileRevisionAsync(claim.TeamId, revision.StorageProfileId, revision.Revision, StorageProfileEligibility.Write, cancellationToken).ConfigureAwait(false);

        return new ResumeDestination(revision.StorageProfileId, revision.Revision, resolved.Problem);
    }

    private async Task<ResumeOutcome> FinishAsync(IntentSnapshot claim, StorageRuntimeDriverLease driverLease, CancellationToken cancellationToken)
    {
        var input = new ValidTransfer(claim.Digest, claim.Size, ResumeOperationTimeout);
        var fence = await LocationFenceAsync(claim, cancellationToken).ConfigureAwait(false);

        var presence = await PresentAsync(claim, driverLease, input, cancellationToken).ConfigureAwait(false);
        if (presence.Problem != null) return await SettleAsync(claim, presence.Problem, presence.ObjectPresent, cancellationToken).ConfigureAwait(false);

        var verifying = await AdvanceToVerifyingAsync(claim, cancellationToken).ConfigureAwait(false);
        if (verifying == null) return ResumeOutcome.Contended;

        var verification = await VerifyAsync(driverLease, claim.ObjectKey, input, new LeaseRenewal(verifying, SystemUsers.SeederId), cancellationToken).ConfigureAwait(false);
        if (verification.Problem != null) return await SettleAsync(verifying, verification.Problem, true, cancellationToken).ConfigureAwait(false);

        return Outcome(await CommitAsync(verifying, SystemUsers.SeederId, verification.Metadata!, fence, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Whether the destination is holding the exact object this transfer meant to write.
    ///
    /// <para><c>Missing</c> is the one answer a resumer reads differently from a writer. A writer maps it as retryable
    /// because it still has the stream and can simply upload again; a resumer has no bytes at all, so for THIS intent
    /// the object being absent is final rather than something a later pass could improve on.</para>
    /// </summary>
    private async Task<ResumePresence> PresentAsync(IntentSnapshot claim, StorageRuntimeDriverLease driverLease, ValidTransfer input, CancellationToken cancellationToken)
    {
        if (!await RenewLeaseAsync(claim, SystemUsers.SeederId, input.Timeout, cancellationToken).ConfigureAwait(false))
            return new ResumePresence(false, Problem(ArtifactCasProblemCode.StaleWorker, true));

        var head = await InvokeAsync(token => driverLease.Driver.HeadAsync(new ArtifactStorageHeadRequest(claim.ObjectKey), token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (head.Problem != null) return new ResumePresence(false, head.Problem);
        if (head.Timeout) return new ResumePresence(false, Problem(ArtifactCasProblemCode.ProviderTimeout, true));

        if (head.Value!.Error is { Code: ArtifactStorageErrorCode.Missing }) return new ResumePresence(false, Problem(ArtifactCasProblemCode.TargetMissing));
        if (head.Value.Error != null) return new ResumePresence(false, Map(head.Value.Error));
        if (!HeadCanMatch(claim.ObjectKey, input, head.Value.Metadata!)) return new ResumePresence(true, Problem(ArtifactCasProblemCode.TargetCorrupt));

        return new ResumePresence(true, null);
    }

    /// <summary>Walks the saga to the only state a verified readback may commit from. Null means the claim was superseded mid-walk.</summary>
    private async Task<IntentSnapshot?> AdvanceToVerifyingAsync(IntentSnapshot claim, CancellationToken cancellationToken)
    {
        var current = claim;
        foreach (var next in ResumeLadder(current.State))
        {
            current = await TransitionAsync(current, next, SystemUsers.SeederId, cancellationToken).ConfigureAwait(false);

            if (current.IsStale) return null;
        }

        return current.State == ArtifactTransferState.Verifying ? current : null;
    }

    /// <summary>The transitions 0131's whitelist admits from each resumable state, in order. The bytes are already at the destination, so the upload leg is walked rather than performed.</summary>
    private static IReadOnlyList<ArtifactTransferState> ResumeLadder(ArtifactTransferState state) => state switch
    {
        ArtifactTransferState.Intended => [ArtifactTransferState.Uploading, ArtifactTransferState.Uploaded, ArtifactTransferState.Verifying],
        ArtifactTransferState.Uploading => [ArtifactTransferState.Uploaded, ArtifactTransferState.Verifying],
        ArtifactTransferState.Uploaded => [ArtifactTransferState.Verifying],
        ArtifactTransferState.RetryScheduled => [ArtifactTransferState.Verifying],
        _ => [],
    };

    /// <summary>
    /// Closes the transfer, but only on an answer that leaves no room for doubt.
    ///
    /// <para>Two independent lines have to be crossed, because they answer different questions. RETRYABLE is the
    /// WRITER's line: whether the same call could succeed if repeated. <see cref="AnswersForTheObject"/> is THIS
    /// INTENT's line: whether the destination said anything about the object at all. Both must be crossed, and
    /// neither implies the other — the codes a writer treats as final are mostly facts about a destination, and a
    /// destination is repairable while <c>Failed</c> is a one-way door the database will not reopen.</para>
    ///
    /// <para>Anything short of that leaves the intent exactly as it was — the claim's lease lapses on its own and a
    /// later pass re-asks. Scheduling a retry instead is equally not an option: that releases the lease, which is the
    /// one thing this sweep looks for, so the intent would be parked exactly where it started.</para>
    /// </summary>
    private async Task<ResumeOutcome> SettleAsync(IntentSnapshot claim, ArtifactCasProblem problem, bool objectPresent, CancellationToken cancellationToken)
    {
        if (problem.IsRetryable) return ResumeOutcome.Inconclusive;

        if (!AnswersForTheObject(problem.Code)) return Unresumable(claim.Id, problem);

        var settled = await HandleProblemAsync(claim, SystemUsers.SeederId, problem, cancellationToken).ConfigureAwait(false);

        // Anything but a rejection means the intent moved under this pass, so it settled nothing and owns no verdict.
        if (settled is not ArtifactCasTransferResult.Rejected) return ResumeOutcome.Contended;

        return objectPresent && await ReportUnreachableAsync(claim, problem, cancellationToken).ConfigureAwait(false)
            ? ResumeOutcome.Orphaned
            : ResumeOutcome.Settled;
    }

    /// <summary>
    /// Whether a problem is the destination answering about THIS OBJECT — the only kind of answer allowed to close an
    /// intent for good.
    ///
    /// <para>Non-retryable is the writer's line and it is not this one. A writer that cannot reach its destination has
    /// a caller holding the bytes and may simply fail the call; a resumer holds an intent whose bytes may be sitting
    /// exactly where it says, and settling it deletes the only way anyone ever finishes that transfer. So a profile
    /// the team disabled, a credential mid-rotation, a provider module missing from THIS worker's image, a driver
    /// without the read capability, an unattributed provider fault — every one of them is a fact about this pod and
    /// this minute, true of every abandoned transfer on that destination at once, and none of them is evidence about
    /// the object. They leave the intent claimable and the next pass asks again.</para>
    ///
    /// <para>Enumerated rather than defaulted, and with no catch-all arm on purpose: a code added later must be put on
    /// one side of this line deliberately, because the wrong default silently burns recoverable transfers
    /// deployment-wide. The refusal below is caught with every other resume fault and reported as inconclusive, so an
    /// unclassified code fails toward keeping the transfer rather than toward destroying it.</para>
    /// </summary>
    private static bool AnswersForTheObject(ArtifactCasProblemCode code) => code switch
    {
        // ─── About the object. The destination looked at this transfer's own key and will say the same next pass. ───
        ArtifactCasProblemCode.TargetMissing => true,        // not there, and a resumer has no bytes to put there
        ArtifactCasProblemCode.TargetCorrupt => true,        // there, but it is demonstrably not this content
        ArtifactCasProblemCode.IdempotencyConflict => true,  // that key is already bound to something else

        // ─── About the destination, the credential, the profile or this pod. All repairable; none of them evidence. ───
        ArtifactCasProblemCode.ProfileMissing => false,
        ArtifactCasProblemCode.ProfileNotActive => false,
        ArtifactCasProblemCode.ProfileRevisionMissing => false,
        ArtifactCasProblemCode.ProfileInvalid => false,
        ArtifactCasProblemCode.ProviderUnavailable => false,
        ArtifactCasProblemCode.CredentialUnavailable => false,
        ArtifactCasProblemCode.CredentialInvalid => false,
        ArtifactCasProblemCode.CredentialBrokerUnavailable => false,
        ArtifactCasProblemCode.ExecutionAdmissionUnavailable => false,
        ArtifactCasProblemCode.Unauthorized => false,
        ArtifactCasProblemCode.Forbidden => false,
        ArtifactCasProblemCode.Unsupported => false,
        ArtifactCasProblemCode.Throttled => false,
        ArtifactCasProblemCode.ProviderTimeout => false,
        ArtifactCasProblemCode.ProviderUnavailableTransient => false,
        ArtifactCasProblemCode.ProviderFailure => false,
        ArtifactCasProblemCode.StaleWorker => false,
        ArtifactCasProblemCode.TransferInProgress => false,

        // ─── About some OTHER row. Neither reachable from a resume nor an answer this transfer may be closed on. ───
        ArtifactCasProblemCode.ArtifactMissing => false,
        ArtifactCasProblemCode.LocationUnavailable => false,

        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "A new CAS problem code must be placed on one side of the resume settle line deliberately."),
    };

    /// <summary>Leaves the transfer's saga exactly as it was and says why. Only this pass's claim was written, and that lease lapses on its own, so the transfer comes back to a later pass instead of being burned on what this one could not reach.</summary>
    private ResumeOutcome Unresumable(Guid intentId, ArtifactCasProblem problem)
    {
        _logger.LogWarning("Abandoned transfer {IntentId} was left unsettled this pass: {Problem} is a fact about the destination, not an answer about its object, and a later pass will re-ask once this claim's lease lapses", intentId, problem.Code);

        return ResumeOutcome.Inconclusive;
    }

    /// <summary>
    /// Names bytes the destination is holding that nothing in the ledger can reach.
    ///
    /// <para>Every mechanism that could act on them — the location verifier, the placement integrity reader, profile
    /// abandonment, the retirement gate — starts from <c>artifact_location</c>, and a transfer that never committed
    /// wrote no row there. So the durable record IS the settled intent: the table refuses DELETE by trigger, and the
    /// row permanently carries the team, the profile revision, the locator, the object key and the size those bytes
    /// were supposed to have. The warning puts the same coordinates in front of an operator.</para>
    ///
    /// <para>Deliberately not a location row of its own. This transfer failed to prove the object is its content, and
    /// a placement asserting otherwise would give every one of those readers a worse answer than none.</para>
    /// </summary>
    private async Task<bool> ReportUnreachableAsync(IntentSnapshot claim, ArtifactCasProblem problem, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var named = await db.ArtifactLocation.AsNoTracking().AnyAsync(value => value.TeamId == claim.TeamId && value.StorageProfileRevisionId == claim.ProfileRevisionId && value.ObjectKey == claim.ObjectKey, cancellationToken).ConfigureAwait(false);

        if (named) return false;

        _logger.LogWarning(
            "Abandoned transfer {IntentId} for team {TeamId} settled as {Problem}, but its destination still holds {ObjectKey} at {Locator} under profile revision {ProfileRevisionId}; no artifact_location names those bytes, so no verifier, placement reader or retirement gate can see them",
            claim.Id, claim.TeamId, problem.Code, claim.ObjectKey, claim.Locator, claim.ProfileRevisionId);

        return true;
    }

    /// <summary>What the shared commit did. A rejection has already settled the intent terminally; anything else left it where a later pass can re-ask.</summary>
    private static ResumeOutcome Outcome(ArtifactCasTransferResult result) => result switch
    {
        ArtifactCasTransferResult.Committed => ResumeOutcome.Committed,
        ArtifactCasTransferResult.Rejected => ResumeOutcome.Settled,
        _ => ResumeOutcome.Inconclusive,
    };

    private sealed record ResumeCandidate(Guid TeamId, Guid Id);
    private sealed record ResumeDestination(Guid ProfileId, int Revision, ArtifactCasProblem? Problem);
    private sealed record ResumePresence(bool ObjectPresent, ArtifactCasProblem? Problem);

    private enum ResumeOutcome { Committed, Settled, Orphaned, Inconclusive, Contended }

    private sealed class ResumeCounts
    {
        private int _committed;
        private int _settled;
        private int _orphaned;
        private int _inconclusive;
        private int _contended;

        public void Record(ResumeOutcome outcome)
        {
            switch (outcome)
            {
                case ResumeOutcome.Committed: _committed++; break;
                case ResumeOutcome.Inconclusive: _inconclusive++; break;
                case ResumeOutcome.Contended: _contended++; break;
                case ResumeOutcome.Orphaned: _orphaned++; _settled++; break;
                default: _settled++; break;
            }
        }

        public ArtifactTransferResumeSummary Summary(int examined) => new()
        {
            Examined = examined, Committed = _committed, Settled = _settled,
            Orphaned = _orphaned, Inconclusive = _inconclusive, Contended = _contended,
        };
    }
}
