using System.Security.Cryptography;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Profile-pinned streaming transfer/read coordinator for the additive CAS v2 tables. Provider I/O is deliberately
/// outside database transactions; durable intent + monotonic revision/fence claims make every commit replay-safe.
/// </summary>
public sealed partial class ArtifactCasRuntimeCoordinator : IArtifactCasRuntimeCoordinator, IArtifactCasRangeReader, IArtifactCasPurgeCoordinator, IArtifactCasTransferResumer
{
    private static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaximumOperationTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MinimumLeaseMargin = TimeSpan.FromMilliseconds(250);
    private const int HashBufferSize = 128 * 1024;

    /// <summary>
    /// How many times one call re-observes an object whose provider-minted tokens moved between its HEAD and the
    /// readback that HEAD licensed, before it gives up. It is what protects a call from a concurrent writer of
    /// IDENTICAL content: every such overwrite trips that fence once, and each one is another reviver finishing the
    /// same repair, so the observation taken after the last of them succeeds.
    ///
    /// <para>THREE sites re-observe, and they are not all of the sites that take a HEAD and then a readback pinned to
    /// it. The two reads (<see cref="OpenObjectAsync"/>, <see cref="OpenWindowAsync"/>) keep, exactly, the verdict
    /// they had before this: a destination that never settles is <c>TargetCorrupt</c> and non-retryable, only said
    /// after a bounded chance to settle rather than on the first trip. <see cref="VerifyRevivalAsync"/> answers a
    /// retryable provider fault, and that verdict replaces nothing — a revival is new, so this is the only answer it
    /// has ever had.</para>
    ///
    /// <para>The FOURTH such site deliberately does not re-observe at all. An ordinary transfer's
    /// <see cref="VerifyAsync"/> takes its observation once and, for the same never-settling destination, still
    /// closes the intent with the non-retryable <c>TargetCorrupt</c> it gave before any of this existed. It CAN meet
    /// a concurrent writer of identical content — a lease claims the intent row, not the key, so a reviver repairing
    /// the placement at that key moves the token this readback is pinned to; see <see cref="VerifyAsync"/> for the
    /// interleaving and why its refusal is safe. What keeps the loop out of it is that verdict, not impossibility:
    /// re-observing there would move an ordinary write's terminal answer from <c>Failed</c> to an unbounded retry to
    /// buy one saved attempt in that window, and the refusal already does not stick to the CONTENT — the next
    /// presentation mints the next generation and commits.</para>
    ///
    /// <para>For a destination that NEVER settles, re-observing changes only how long a call waits before answering:
    /// what convicts the content is compared on every single observation and refuses immediately, and the verdict at
    /// the end of the budget is the one each site already gave. For a destination that settles on a DIFFERENT object
    /// of the same length, it changes the answer, and the one path with nothing behind this fence pays for it — named
    /// as a consequence on <see cref="DriveRangeAsync"/> and on <see cref="LicensedStreamAsync"/>.</para>
    /// </summary>
    internal const int MaximumObservationAttempts = 3;

    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IStorageRuntimeDriverBroker _driverBroker;
    private readonly TimeProvider _clock;
    private readonly ILogger<ArtifactCasRuntimeCoordinator> _logger;

    public ArtifactCasRuntimeCoordinator(DbContextOptions<CodeSpaceDbContext> dbOptions, IStorageRuntimeDriverBroker driverBroker, TimeProvider clock, ILogger<ArtifactCasRuntimeCoordinator> logger)
    {
        _dbOptions = dbOptions;
        _driverBroker = driverBroker;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ArtifactCasTransferResult> PutAsync(ArtifactCasTransferRequest request, CancellationToken cancellationToken)
    {
        var input = Validate(request);
        // Caller-supplied lineage is identity, not authority. Until an authoritative active-attempt adapter exists,
        // accepting it would let a replaced/zombie attempt mint a fresh effect intent.
        if (request.ExecutionIdentity != null)
            return new ArtifactCasTransferResult.Rejected(null, Problem(ArtifactCasProblemCode.ExecutionAdmissionUnavailable));
        var resolved = await ResolveProfileRevisionAsync(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, StorageProfileEligibility.Write, cancellationToken).ConfigureAwait(false);
        if (resolved.Problem != null) return new ArtifactCasTransferResult.Rejected(null, resolved.Problem);

        var intent = await EnsureIntentAsync(request, resolved.ProfileRevisionId!.Value, input.Digest, cancellationToken).ConfigureAwait(false);
        if (intent.Revive != null) return await ReviveAsync(request, input, intent, cancellationToken).ConfigureAwait(false);
        if (intent.Problem != null) return new ArtifactCasTransferResult.Rejected(intent.Id, intent.Problem);
        if (intent.State == ArtifactTransferState.Committed)
            return new ArtifactCasTransferResult.Committed(intent.Id, intent.ArtifactObjectId!.Value, intent.ArtifactLocationId!.Value, true);
        if (intent.State is ArtifactTransferState.Failed or ArtifactTransferState.Cancelled)
            return new ArtifactCasTransferResult.Rejected(intent.Id, StoredProblem(intent));

        // A scheduled retry's backoff is deliberately NOT pre-checked here. next_attempt_at is stamped by the
        // database, and the claim statement below judges it against that same clock inside one statement; a second
        // gate read against this pod's wall clock could only ever disagree with the one that decides.
        var claimed = await ClaimAsync(request.TeamId, intent.Id, request.ActorId, input.Timeout, cancellationToken).ConfigureAwait(false);
        var claim = claimed.Intent;
        if (claim.State == ArtifactTransferState.Committed)
            return new ArtifactCasTransferResult.Committed(claim.Id, claim.ArtifactObjectId!.Value, claim.ArtifactLocationId!.Value, true);
        if (claim.State is ArtifactTransferState.Failed or ArtifactTransferState.Cancelled)
            return new ArtifactCasTransferResult.Rejected(claim.Id, StoredProblem(claim));
        if (!claimed.Acquired) return Refused(claimed);

        StorageRuntimeDriverLease? driverLease = null;
        try
        {
            if (claim.State == ArtifactTransferState.RetryScheduled)
            {
                claim = await TransitionAsync(claim, ArtifactTransferState.Uploading, request.ActorId, cancellationToken).ConfigureAwait(false);
                if (claim.IsStale) return Stale(intent.Id);
            }
            var create = await OpenDriverAsync(new DriverActivationRequest(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, StorageProfileEligibility.Write, input.Timeout, StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.ConditionalCreate), cancellationToken).ConfigureAwait(false);
            if (create.Problem != null) return await HandleProblemAsync(claim, request.ActorId, create.Problem, cancellationToken).ConfigureAwait(false);
            driverLease = create.Lease!;
            return await DriveTransferAsync(request, input, claim, driverLease, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (driverLease != null) await DisposeLeaseQuietlyAsync(driverLease).ConfigureAwait(false);
        }
    }

    public async Task<ArtifactCasReadResult> OpenReadAsync(ArtifactCasReadRequest request, CancellationToken cancellationToken)
    {
        var timeout = Validate(request);
        var resolved = await ResolveProfileRevisionAsync(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, StorageProfileEligibility.Read, cancellationToken).ConfigureAwait(false);
        if (resolved.Problem != null) return new ArtifactCasReadResult.Unavailable(resolved.Problem);

        var stored = await StoredLocationAsync(request.TeamId, request.ArtifactObjectId, resolved.ProfileRevisionId!.Value, cancellationToken).ConfigureAwait(false);
        if (stored == null) return new ArtifactCasReadResult.Unavailable(Problem(ArtifactCasProblemCode.ArtifactMissing));
        var create = await OpenDriverAsync(new DriverActivationRequest(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, StorageProfileEligibility.Read, timeout, StorageProviderCapabilities.StreamingRead), cancellationToken).ConfigureAwait(false);
        if (create.Problem != null) return new ArtifactCasReadResult.Unavailable(create.Problem);

        StorageRuntimeDriverLease? driverLease = create.Lease!;
        try
        {
            for (var attempt = 0; attempt < MaximumObservationAttempts; attempt++)
            {
                var opened = await OpenObjectAsync(driverLease, stored, timeout, cancellationToken).ConfigureAwait(false);
                if (opened == null) continue;
                if (opened.Problem != null) return new ArtifactCasReadResult.Unavailable(opened.Problem);

                var stream = new ArtifactCasVerifyingReadStream(opened.Value!, driverLease, stored.Size, stored.Digest);
                driverLease = null;
                return new ArtifactCasReadResult.Opened(stream, stored.Size, Convert.ToHexStringLower(stored.Digest));
            }

            return new ArtifactCasReadResult.Unavailable(Problem(ArtifactCasProblemCode.TargetCorrupt));
        }
        finally
        {
            if (driverLease != null) await DisposeLeaseQuietlyAsync(driverLease).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One HEAD-then-open of the recorded object, or null when the destination rewrote it between those two calls and
    /// the pair has to be taken again. The verdict when re-observing runs out is the caller's, and it is the
    /// <c>TargetCorrupt</c> this path has always given: a destination that will not hold still through a HEAD and an
    /// open is refused exactly as before, only after being given a bounded chance to settle.
    /// </summary>
    private static async Task<Invocation<Stream>?> OpenObjectAsync(StorageRuntimeDriverLease driverLease, ReadLocation stored, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var driver = driverLease.Driver;
        var head = await InvokeAsync(token => driver.HeadAsync(new ArtifactStorageHeadRequest(stored.ObjectKey), token), timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (head.Problem != null) return new Invocation<Stream>(null, false, head.Problem);
        if (head.Timeout) return new Invocation<Stream>(null, true, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (head.Value?.Error != null) return new Invocation<Stream>(null, false, Map(head.Value.Error, readMissing: true));
        if (!MetadataMatches(stored, head.Value!.Metadata!, driver.Capabilities)) return new Invocation<Stream>(null, false, Problem(ArtifactCasProblemCode.TargetCorrupt));

        var opened = await InvokeAsync(token => driver.OpenReadAsync(new ArtifactStorageReadRequest(stored.ObjectKey)
        {
            ExpectedETag = DurableETag(stored.ProviderETag, driver.Capabilities),
            ExpectedVersion = stored.ProviderObjectVersion,
        }, token), timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (opened.Problem != null) return new Invocation<Stream>(null, false, opened.Problem);
        if (opened.Timeout) return new Invocation<Stream>(null, true, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (opened.Value?.Error != null) return new Invocation<Stream>(null, false, Map(opened.Value.Error, readMissing: true));

        var lengthAgrees = opened.Value!.ContentLength == stored.Size && opened.Value.TotalLength == stored.Size;

        return await LicensedStreamAsync(opened.Value, head.Value.Metadata!, stored.ObjectKey, lengthAgrees).ConfigureAwait(false);
    }

    private async Task<ArtifactCasTransferResult> DriveTransferAsync(ArtifactCasTransferRequest request, ValidTransfer input, IntentSnapshot claim, StorageRuntimeDriverLease driverLease, CancellationToken cancellationToken)
    {
        var driver = driverLease.Driver;
        var fence = await LocationFenceAsync(claim, cancellationToken).ConfigureAwait(false);
        var current = claim;
        if (current.State is ArtifactTransferState.Intended or ArtifactTransferState.RetryScheduled)
        {
            current = await TransitionAsync(current, ArtifactTransferState.Uploading, request.ActorId, cancellationToken).ConfigureAwait(false);
            if (current.IsStale) return Stale(claim.Id);
        }

        if (current.State == ArtifactTransferState.Uploading)
        {
            if (!await RenewLeaseAsync(current, request.ActorId, input.Timeout, cancellationToken).ConfigureAwait(false)) return Stale(claim.Id);
            var head = await InvokeAsync(token => driver.HeadAsync(new ArtifactStorageHeadRequest(request.TargetObjectKey), token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
            if (head.Problem != null) return await HandleProblemAsync(current, request.ActorId, head.Problem, cancellationToken).ConfigureAwait(false);
            if (head.Timeout) return await HandleProblemAsync(current, request.ActorId, Problem(ArtifactCasProblemCode.ProviderTimeout, true), cancellationToken).ConfigureAwait(false);
            if (head.Value!.IsSuccess)
            {
                if (!HeadCanMatch(request.TargetObjectKey, input, head.Value.Metadata!))
                    return await HandleProblemAsync(current, request.ActorId, Problem(ArtifactCasProblemCode.TargetCorrupt), cancellationToken).ConfigureAwait(false);
            }
            else if (head.Value.Error!.Code == ArtifactStorageErrorCode.Missing)
            {
                if (!await RenewLeaseAsync(current, request.ActorId, input.Timeout, cancellationToken).ConfigureAwait(false)) return Stale(claim.Id);
                var put = await InvokeOwnedInputAsync(token => driver.PutAsync(new ArtifactStoragePutRequest(request.TargetObjectKey, request.Content)
                {
                    ContentLength = request.ExpectedSizeBytes,
                    ExpectedSha256 = request.ExpectedSha256,
                    ContentType = request.ContentType,
                    Condition = ArtifactStorageWriteCondition.CreateOnly,
                }, token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
                if (put.Problem != null) return await HandleProblemAsync(current, request.ActorId, put.Problem, cancellationToken).ConfigureAwait(false);
                if (put.Timeout) return await HandleProblemAsync(current, request.ActorId, Problem(ArtifactCasProblemCode.ProviderTimeout, true), cancellationToken).ConfigureAwait(false);
                if (!put.Value!.IsSuccess && put.Value.Error!.Code != ArtifactStorageErrorCode.AlreadyExists)
                    return await HandleProblemAsync(current, request.ActorId, Map(put.Value.Error), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                return await HandleProblemAsync(current, request.ActorId, Map(head.Value.Error), cancellationToken).ConfigureAwait(false);
            }

            current = await TransitionAsync(current, ArtifactTransferState.Uploaded, request.ActorId, cancellationToken).ConfigureAwait(false);
            if (current.IsStale) return Stale(claim.Id);
        }

        if (current.State == ArtifactTransferState.Uploaded)
        {
            current = await TransitionAsync(current, ArtifactTransferState.Verifying, request.ActorId, cancellationToken).ConfigureAwait(false);
            if (current.IsStale) return Stale(claim.Id);
        }

        if (current.State != ArtifactTransferState.Verifying)
            return new ArtifactCasTransferResult.Rejected(current.Id, Problem(ArtifactCasProblemCode.ProviderFailure));

        var verification = await VerifyAsync(driverLease, request.TargetObjectKey, input, new LeaseRenewal(current, request.ActorId), cancellationToken).ConfigureAwait(false);
        if (verification.Problem != null) return await HandleProblemAsync(current, request.ActorId, verification.Problem, cancellationToken).ConfigureAwait(false);
        return await CommitAsync(current, request.ActorId, verification.Metadata!, fence, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts a placement that lost its bytes back into service, by re-driving the intent that already names it.
    ///
    /// <para>A revival is the SAME content going to the same object key under the same profile revision. The identity
    /// is unchanged, and the committed intent already names both the object and the placement — so the repair is that
    /// intent driven through a fresh upload and the fenced commit, not a new one.</para>
    ///
    /// <para>A new GENERATION would have been the obvious shape and is wrong twice over. It would have to come from
    /// widening what <see cref="Spent"/> counts, and then a <c>Missing</c>, <c>Corrupt</c> or mid-purge placement pays
    /// a full provider round trip AND burns a generation on every attempt, for as long as the destination stays
    /// broken — an unbounded run of intent rows for one payload. Worse, the signal stops being monotone: a revived
    /// placement is <c>Available</c> again, which UN-spends its generation, so any rule that counts spent generations
    /// falls back onto an earlier <c>Failed</c> one and hands every later writer of this content that dead intent's
    /// hard rejection — for content that is by then perfectly stored. Re-driving costs neither: the ledger grows by
    /// nothing, and the only durable record of the repair is the placement's own append-only observation.</para>
    ///
    /// <para>No worker lease is taken or renewed anywhere on this path, because the intent is terminally
    /// <c>Committed</c> and 0131 lets nothing claim or move it, so nothing serializes N concurrent revivals of one
    /// placement and each of them pays the full repair. That is not a cheap duplicate: every one of them streams the
    /// whole object back and re-hashes it, and on the <c>Corrupt</c> arm every one of them also uploads it, since an
    /// overwrite has no <c>CreateOnly</c> short-circuit to fall into. Only the commit is arbitrated — the placement
    /// fence decides which observation is recorded, and the losers are answered <see cref="Stale"/>, which is exactly
    /// what a <c>Deferred</c> means to the caller's wait loop: somebody else is storing your bytes, come back. Their
    /// next attempt reads the now-<c>Available</c> placement straight from the ledger, so the waste is bounded by one
    /// round of it. A lease would collapse that to one payer and is deliberately not taken: it would need a claim on
    /// a row the database forbids claiming, and the cost is paid only while a destination is actually broken.</para>
    ///
    /// <para>A PROVIDER fault is reported instead of deferred, even a retryable one. Having no lease also means having
    /// nowhere to park a backoff — the intent is terminal, so <see cref="HandleProblemAsync"/>'s durable
    /// <c>next_attempt_at</c> is unavailable — and answering <c>Deferred</c> without one would turn that same wait
    /// loop into an un-throttled retry loop, hammering a broken destination with a full round trip every poll for the
    /// caller's whole budget. The caller is told what went wrong and decides; the write it replaces refused outright
    /// in every one of these cases anyway.</para>
    /// </summary>
    private async Task<ArtifactCasTransferResult> ReviveAsync(ArtifactCasTransferRequest request, ValidTransfer input, IntentSnapshot intent, CancellationToken cancellationToken)
    {
        var create = await OpenDriverAsync(new DriverActivationRequest(request.TeamId, request.StorageProfileId, request.StorageProfileRevision, StorageProfileEligibility.Write, input.Timeout, StorageProviderCapabilities.StreamingWrite | StorageProviderCapabilities.StreamingRead | StorageProviderCapabilities.ConditionalCreate), cancellationToken).ConfigureAwait(false);
        if (create.Problem != null) return new ArtifactCasTransferResult.Rejected(intent.Id, create.Problem);

        var driverLease = create.Lease!;
        try
        {
            var placed = await RepairObjectAsync(driverLease, request, input, intent.Revive!, cancellationToken).ConfigureAwait(false);
            if (placed != null) return new ArtifactCasTransferResult.Rejected(intent.Id, placed);

            var verification = await VerifyRevivalAsync(driverLease, request.TargetObjectKey, input, cancellationToken).ConfigureAwait(false);
            if (verification.Problem != null) return new ArtifactCasTransferResult.Rejected(intent.Id, verification.Problem);

            return await ReviveLocationAsync(intent, request.ActorId, verification.Metadata!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await DisposeLeaseQuietlyAsync(driverLease).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets the object back to the key, by whichever repair the placement's own evidence licenses. The two arms are
    /// not a mode a caller picks: each state SAYS what the destination was last observed doing, and a repair may only
    /// do what its state's evidence justifies.
    ///
    /// <para><c>Corrupt</c> is the durable record that the destination was caught serving something that is not this
    /// object, against a recorded observation the demotion leaves untouched and therefore still true. That is
    /// standing permission to REPLACE what is at the key, and nothing weaker repairs it: the foreign object answers a
    /// HEAD, so a create-only repair skips its upload, fails its readback on those same bytes, and refuses forever.
    /// The evidence is also what bounds the overwrite — it names this one key, so this one key is all it may
    /// overwrite.</para>
    ///
    /// <para><c>Missing</c> and <c>Purged</c> say the opposite: nothing is at the key. An object found there is a
    /// surprise no observation of theirs accounts for, so it is HEADed and refused unless it is already this exact
    /// content — the same answer an ordinary first write gives a key it did not expect to be occupied. Reading the
    /// two as one rule and overwriting on both would let a stale <c>Missing</c> row destroy a stranger's object.</para>
    /// </summary>
    private static Task<ArtifactCasProblem?> RepairObjectAsync(StorageRuntimeDriverLease driverLease, ArtifactCasTransferRequest request, ValidTransfer input, LocationFence revive, CancellationToken cancellationToken) =>
        HoldsAnotherObject(revive.State)
            ? ReplaceObjectAsync(driverLease, request, input, cancellationToken)
            : PlaceObjectAsync(driverLease, request, input, cancellationToken);

    /// <summary>
    /// Whether the placement's own record says the destination is serving something that is NOT this object. That is
    /// the single piece of evidence which licenses overwriting a key rather than filling one believed empty, and
    /// <c>Corrupt</c> is the only state that carries it: <c>ArtifactLocationVerifier</c> reaches it only by catching
    /// the destination disagreeing with an observation that stays recorded, and stays true, afterwards.
    ///
    /// <para>A named predicate rather than a comparison inlined at the branch, so the exhaustive table over every
    /// location state decides THIS as well as the whitelist. Passing the whitelist is not the same question: a state
    /// admitted there later would inherit the create-only repair by silence, and for anything meaning "the wrong bytes
    /// are present" that repair is the dead end this arm exists to remove — it HEADs the impostor, skips its upload,
    /// and fails its own readback for as long as the placement lasts.</para>
    /// </summary>
    internal static bool HoldsAnotherObject(ArtifactLocationState state) => state == ArtifactLocationState.Corrupt;

    /// <summary>
    /// Writes the object over whatever is at the key, unconditionally.
    ///
    /// <para>No HEAD precedes it, because there is nothing a HEAD could add: this is only ever reached for a placement
    /// already recorded as holding the wrong object, and every answer a HEAD could give — the foreign bytes, this
    /// content, or nothing at all — is repaired by the same upload. Skipping it also removes the window in which a HEAD
    /// agrees and the object changes before the PUT.</para>
    ///
    /// <para><c>AlreadyExists</c> is deliberately NOT tolerated the way the create-only arm tolerates it. Under
    /// <c>None</c> a provider that reports it has refused to overwrite rather than raced a twin, and treating that as
    /// success would hand the commit a readback of bytes this attempt never wrote.</para>
    ///
    /// <para>This is the first CAS write that can rewrite an object a COMMITTED placement already records, so what a
    /// LOSING reviver leaves behind is a real question. Only one party can leave anything — the arm is dispatched on a
    /// <c>Corrupt</c> fence, so nothing that reads an <c>Available</c> placement ever arrives here, and every other
    /// writer of this key is create-only — and it is a second reviver of the same broken placement, PUTting the SAME
    /// bytes. Where that lands decides which fences it can reach.</para>
    ///
    /// <para>THE SEARCH for every fence this PUT could reach — every place a provider-minted or recorded field is
    /// compared against another reading of it — because three earlier attempts at this list each enumerated it from
    /// memory and each missed a member. Over <c>backend/src/CodeSpace.Core/Services/Workflows/Artifacts</c>, run one
    /// <c>grep -rnE</c> alternating <c>ProviderETag</c>, <c>ProviderObjectVersion</c>, <c>ExpectedETag</c>,
    /// <c>ExpectedVersion</c>, <c>ObservedSizeBytes</c>, <c>ProviderChecksum</c>, <c>\.ETag</c> and <c>\.Version\b</c>
    /// — the last two matter, because the raw head-vs-open comparison carries none of the recorded names and every
    /// earlier list, searching for those alone, could not have found it. Then follow the two shared predicates the
    /// search lands on — <see cref="MetadataMatches"/> and <see cref="ProviderTokensAgree"/> — to their call sites.
    /// Keep the hits that COMPARE two readings or SEND one as a precondition, since a precondition is a comparison the
    /// provider performs. Everything else the search returns
    /// is not a comparison and is dismissed by that filter rather than by this list: property and record declarations,
    /// capability flags, the local driver's ETag minting, assignments and projections onto a row or a claim, argument
    /// validation that only asks whether a pin is null, and OSS's own copy-result plumbing. NINETEEN comparison sites
    /// survive, in five groups, and every one is marked below. Sites are named by method so the list does not rot;
    /// re-running the search is what proves it still complete.</para>
    ///
    /// <para>GROUP 1 — IN FAMILY: a RECORDED value against a freshly read one. FIVE sites.
    /// <see cref="MetadataMatches"/>, reached from the whole-object read's HEAD (<see cref="OpenObjectAsync"/>) and from the range read's
    /// (<c>HeadForRangeAsync</c>); the stored pin each of those two sends to its open (<see cref="OpenObjectAsync"/>,
    /// <see cref="OpenWindowAsync"/>); and the purge's delete precondition
    /// (<c>ArtifactCasRuntimeCoordinator.Purge.cs · DeleteAsync</c>). ANSWER: none of the five can be moved by this
    /// PUT. The ETag at all five is read through <see cref="DurableETag"/>, which yields it only from a provider
    /// declaring <c>StableETag</c> — the ETag is derived from the CONTENT, pinned by the conformance kit, which rewrites
    /// the same bytes and requires the same ETag back — so an overwrite of identical bytes reproduces the value where
    /// it is compared, and where it would not, it was discarded before the comparison. The version is never populated
    /// at all: no shipped module declares <c>ObjectVersioning</c> and the conformance kit refuses a version from any
    /// driver that does not, so <see cref="Observe"/> only ever writes null and every one of the five short-circuits.
    /// It arms the day a provider reports a real generation id, which is also the day a byte-identical overwrite
    /// starts minting one.</para>
    ///
    /// <para>GROUP 2 — IN FAMILY: a HEAD against the readback that HEAD licensed, RAW, with no recorded value and
    /// therefore no <see cref="DurableETag"/> between them. THREE sites: the verification
    /// (<see cref="ObserveAsync"/>, which both sends the fresh HEAD's tokens as the open's pin and compares what comes
    /// back), and the two reads, which share the comparison through <see cref="LicensedStreamAsync"/> and reach it
    /// from <see cref="OpenObjectAsync"/> and <see cref="OpenWindowAsync"/>. ANSWER, for the fence itself: nothing
    /// filters out a destination whose ETag is not content-derived, so this PUT moves all three wherever the ETag is
    /// not — local RWX today, where it is the file's mtime; a <c>StableETag</c> destination such as OSS reproduces the
    /// token and never trips them. What each site DOES with the trip is not one answer, so each gets its own below.
    /// The content half (<see cref="ContentAgrees"/>) is untouched at all three: it refuses immediately, on every
    /// observation, wherever a readback describes other bytes than its HEAD did.</para>
    ///
    /// <para>The two READS re-observe (<see cref="MaximumObservationAttempts"/>) and each keeps, exactly, the verdict
    /// it had: a destination that never settles is still the non-retryable <c>TargetCorrupt</c>, now said after a
    /// bounded chance to settle rather than on the first trip. They are the sites that most need it — each reaches the
    /// fence through a placement that was <c>Available</c> when the reader loaded its row, and a false
    /// <c>TargetCorrupt</c> there is worse than a failed write: it tells a consumer that correct bytes are somebody
    /// else's. What the relaxation costs the window path is named on <see cref="DriveRangeAsync"/>.</para>
    ///
    /// <para><see cref="ObserveAsync"/> is the member that DIVERGES, and neither half of the reads' answer is true of
    /// it: it is one comparison with two callers, and they answer a trip oppositely. On a REVIVAL
    /// (<see cref="VerifyRevivalAsync"/>) it is observed again, and at the end of the budget answered with a RETRYABLE
    /// <c>ProviderUnavailableTransient</c> — which keeps no earlier verdict, because nothing could revive a placement
    /// before this. On an ORDINARY transfer (<see cref="VerifyAsync"/>) there is no second observation at all: the
    /// first trip is answered with the non-retryable <c>TargetCorrupt</c> that closed the intent before any of this
    /// existed, and that stays the write's terminal answer. Calling this site "re-observes, keeps its verdict" is the
    /// misreading the header on <see cref="MaximumObservationAttempts"/> was written to stop.</para>
    ///
    /// <para>GROUP 3 — NOT A FENCE for this PUT: a recorded value against ANOTHER RECORDED value. THREE sites, all in
    /// ArtifactCasRuntimeCoordinator.Purge.cs, each re-reading the location row against the purge claim's snapshot of
    /// the same columns — <c>ReleaseAsync</c>, <c>ClaimIsCurrentAsync</c> and <c>FinalizePurgeAsync</c>. REASON: both
    /// sides are ledger columns, so no provider write moves either; the only write that can is a commit, and all three
    /// also pin <c>revision</c>, which every commit advances. They are additionally unreachable from here — each
    /// requires <c>Deleting</c>, which the revival whitelist excludes.</para>
    ///
    /// <para>GROUP 4 — NOT A FENCE for this PUT: a CONTENT-derived recorded value. THREE sites:
    /// <see cref="Verified"/>, and <c>ArtifactLocationVerifier</c>'s recorded checksum and recorded size against a
    /// HEAD (deliberately no ETag there). REASON: content-derived by construction, and a rewrite of IDENTICAL bytes
    /// reproduces content — that is what makes it the same content.</para>
    ///
    /// <para>GROUP 5 — IN FAMILY, as the provider half of the pins above: FIVE driver-side sites that enforce whatever
    /// <c>ExpectedETag</c> the coordinator sent — <c>OpenReadAsync</c> and <c>DeleteAsync</c> in
    /// Providers/Local/LocalRwxArtifactStorageDriverFactory.cs, <c>DeleteAsync</c> and <c>OpenEmptyAsync</c> in
    /// Providers/AliyunOss/AliyunOssArtifactStorageDriver.cs, and the <c>If-Match</c> header <c>OpenRangeAsync</c>
    /// forwards to OSS in that driver's .Http.cs. ANSWER: each compares only what it was handed, so a pin the
    /// coordinator sent as null is never enforced and a group-1 pin cannot be moved by this PUT for the reason given
    /// there. The one pin sent raw is group 2's, and its refusal (<c>ConditionNotMet</c>) is one of the two faces
    /// <see cref="ObserveAsync"/> answers by observing again.</para>
    /// </summary>
    private static async Task<ArtifactCasProblem?> ReplaceObjectAsync(StorageRuntimeDriverLease driverLease, ArtifactCasTransferRequest request, ValidTransfer input, CancellationToken cancellationToken)
    {
        var put = await InvokeOwnedInputAsync(token => driverLease.Driver.PutAsync(new ArtifactStoragePutRequest(request.TargetObjectKey, request.Content)
        {
            ContentLength = request.ExpectedSizeBytes,
            ExpectedSha256 = request.ExpectedSha256,
            ContentType = request.ContentType,
            Condition = ArtifactStorageWriteCondition.None,
        }, token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (put.Problem != null) return put.Problem;
        if (put.Timeout) return Problem(ArtifactCasProblemCode.ProviderTimeout, true);

        return put.Value!.IsSuccess ? null : Map(put.Value.Error!);
    }

    /// <summary>
    /// Makes sure the object is at the key, uploading it only when the destination says nothing is there. Separate
    /// from <see cref="DriveTransferAsync"/>'s own head/put block on purpose: that one interleaves worker-lease
    /// renewals and saga bookkeeping a terminal intent has neither of, and folding them together would have to give
    /// one of the two the other's answer to a lapsed lease.
    /// </summary>
    private static async Task<ArtifactCasProblem?> PlaceObjectAsync(StorageRuntimeDriverLease driverLease, ArtifactCasTransferRequest request, ValidTransfer input, CancellationToken cancellationToken)
    {
        var driver = driverLease.Driver;
        var head = await InvokeAsync(token => driver.HeadAsync(new ArtifactStorageHeadRequest(request.TargetObjectKey), token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (head.Problem != null) return head.Problem;
        if (head.Timeout) return Problem(ArtifactCasProblemCode.ProviderTimeout, true);
        if (head.Value!.IsSuccess)
            return HeadCanMatch(request.TargetObjectKey, input, head.Value.Metadata!) ? null : Problem(ArtifactCasProblemCode.TargetCorrupt);
        if (head.Value.Error!.Code != ArtifactStorageErrorCode.Missing) return Map(head.Value.Error);

        var put = await InvokeOwnedInputAsync(token => driver.PutAsync(new ArtifactStoragePutRequest(request.TargetObjectKey, request.Content)
        {
            ContentLength = request.ExpectedSizeBytes,
            ExpectedSha256 = request.ExpectedSha256,
            ContentType = request.ContentType,
            Condition = ArtifactStorageWriteCondition.CreateOnly,
        }, token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (put.Problem != null) return put.Problem;
        if (put.Timeout) return Problem(ArtifactCasProblemCode.ProviderTimeout, true);

        return put.Value!.IsSuccess || put.Value.Error!.Code == ArtifactStorageErrorCode.AlreadyExists ? null : Map(put.Value.Error);
    }

    /// <summary>
    /// Records this readback onto the placement the intent names, or refuses if anything moved that row since the
    /// fence was read. The fence and this commit both address the row by the intent's own
    /// <c>artifact_location_id</c>, which 0127 pins to the intent and 0150 holds immutable, so they are provably the
    /// same row and the whitelist is comparing what it thinks it is.
    /// </summary>
    private async Task<ArtifactCasTransferResult> ReviveLocationAsync(IntentSnapshot intent, Guid actorId, ArtifactStorageObjectMetadata metadata, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var location = await db.ArtifactLocation.SingleAsync(value => value.TeamId == intent.TeamId && value.Id == intent.ArtifactLocationId, cancellationToken).ConfigureAwait(false);
        if (!Revivable(location, intent.Revive)) return Stale(intent.Id);

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        Observe(location, intent, metadata, actorId, now);
        db.ArtifactLocationEvent.Add(Event(location, actorId));

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        // Both faces of losing to a concurrent reviver: the row's own concurrency token, and
        // ux_artifact_location_event_revision, which the winner's observation already took at this revision.
        catch (Exception exception) when (IsUniqueViolation(exception) || exception is DbUpdateConcurrencyException)
        {
            return Stale(intent.Id);
        }

        return new ArtifactCasTransferResult.Committed(intent.Id, intent.ArtifactObjectId!.Value, location.Id, false);
    }

    /// <summary>
    /// What the location row for this transfer's target looked like BEFORE this attempt touched the provider, or null
    /// when no row existed. It is the only thing that makes reviving a lost placement safe: the writer verifies bytes
    /// it just uploaded, and between that readback and its commit a purge could remove exactly those bytes. A purge
    /// must claim the row with <c>Deleting</c> before it deletes anything (0150 admits <c>Purged</c> from no other
    /// state), and every location write advances <c>revision</c> by exactly one, so "still in the state, and at the
    /// revision, I read before uploading" means no purge ran in that whole window. A purge that removed bytes without
    /// advancing the row is outside what this can see, and outside what the reaper is allowed to do.
    ///
    /// <para>Costs one indexed read on <c>ux_artifact_location_profile_object_key</c>, and only on an attempt that is
    /// about to do provider I/O — a dedup hit returns long before this.</para>
    /// </summary>
    private async Task<LocationFence?> LocationFenceAsync(IntentSnapshot claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        return await db.ArtifactLocation.AsNoTracking()
            .Where(value => value.TeamId == claim.TeamId && value.StorageProfileRevisionId == claim.ProfileRevisionId && value.ObjectKey == claim.ObjectKey)
            .Select(value => new LocationFence(value.State, value.Revision))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// An ORDINARY transfer's readback, proving the object at the key is this content. ONE observation, and an object
    /// that moved out from under it keeps the non-retryable <c>TargetCorrupt</c> this path has always given.
    ///
    /// <para>It does not re-observe, and that is a CHOICE of verdict rather than a proof the interleaving is out of
    /// reach. What the worker lease excludes is every other attempt driving THIS INTENT — 0131 takes the claim on the
    /// intent row — and it says nothing whatever about the object key. Nothing binds an idempotency scope to a key,
    /// <see cref="LocationFenceAsync"/> addresses its fence by the KEY rather than by an intent's location id, and a
    /// revival holds no lease at all. So an ordinary transfer CAN be standing at a head-then-readback while another
    /// party overwrites its key: a second producer of this content fills the key create-only and verifies it, while
    /// the placement's own committed intent repairs a <c>Corrupt</c> record by PUTting the same bytes over it
    /// unconditionally (<see cref="ReplaceObjectAsync"/>, still the ONE write here that overwrites). That PUT moves
    /// the raw provider token this readback is pinned to, on every destination whose ETag is not content-derived.</para>
    ///
    /// <para>What it meets there is this method's <c>TargetCorrupt</c>, and that is SAFE for two reasons. Nothing
    /// wrong is recorded: the fence refuses the commit rather than licensing a readback this attempt cannot vouch for.
    /// And the refusal does not stick to the CONTENT — <see cref="Spent"/> counts the <c>Failed</c> intent, so the
    /// producer's next attempt mints the next generation and re-drives, by which time the reviver's own commit has
    /// left the placement <c>Available</c> holding exactly these bytes and <see cref="Verified"/> admits it. That is
    /// the whole difference from the forever-refusal <see cref="Reusable"/> removes: this trip is a momentary event,
    /// not a standing state, so regenerating clears it instead of meeting it again.
    /// <c>An_ordinary_write_refused_by_a_concurrent_revivers_overwrite_commits_on_its_next_generation</c> drives that
    /// interleaving and pins both halves.</para>
    ///
    /// <para>So the loop WOULD buy this path something, and the amount was measured rather than argued: give
    /// <see cref="VerifyAsync"/> the same re-observation and that test's writer stops being refused and commits. One
    /// saved attempt in that window, then — against the terminal answer it costs:
    /// <see cref="VerifyRevivalAsync"/> gives up RETRYABLE, which <see cref="HandleProblemAsync"/> turns into
    /// <c>RetryScheduled</c> — nothing caps that count or promotes it back to <c>Failed</c> — so a destination that
    /// never settles would stop being closed and park forever on a path no repair reaches. A revival can pay that
    /// price because its intent is already terminal and it has no durable backoff to park on in the first place; an
    /// ordinary write cannot, and one attempt its own next generation re-drives does not buy it back.
    /// <see cref="VerifyRevivalAsync"/> is where the relaxation belongs and where it stays confined.</para>
    /// </summary>
    private async Task<Verification> VerifyAsync(StorageRuntimeDriverLease driverLease, string objectKey, ValidTransfer input, LeaseRenewal renewal, CancellationToken cancellationToken)
    {
        var observed = await ObserveAsync(driverLease, objectKey, input, renewal, cancellationToken).ConfigureAwait(false);

        return observed ?? new Verification(null, Problem(ArtifactCasProblemCode.TargetCorrupt));
    }

    /// <summary>
    /// A REVIVAL's readback, re-observing for as long as the destination keeps moving under the attempt.
    ///
    /// <para>The HEAD and the read it pins are themselves a fence, and the only one here that is INTRA-attempt. The
    /// four a committed placement carries all read a RECORDED value through <see cref="DurableETag"/>, which discards
    /// it unless the provider declares its ETag content-derived; this one compares what this attempt's own HEAD just
    /// reported, raw, so nothing filters out a destination whose ETag is not — and the local driver, deriving one from
    /// the file's mtime, is exactly such a destination. A concurrent reviver's unconditional overwrite of the SAME
    /// bytes therefore moves it, and moves it on the one arm that overwrites at all.</para>
    ///
    /// <para>So a trip is answered by observing again, never by a verdict. All it establishes is that the object at
    /// the key moved between two calls, which is no evidence whatever about the bytes — and a revival must not be
    /// failed by a second reviver writing identical content. A destination that will not hold still for a HEAD and a
    /// read is a transient provider fault and is reported as one. That verdict replaces no earlier one: nothing could
    /// revive a placement before this change, so this loop is the whole of what a revival has ever done here.</para>
    ///
    /// <para>Relaxed for EXACTLY the two provider-minted tokens, <see cref="ProviderTokensAgree"/>, and nothing else.
    /// The object key, the length and the provider's own Sha256 are properties of the bytes rather than of the write
    /// that placed them (<see cref="ContentAgrees"/> says why for each), so no rewrite of identical content can move
    /// one — a disagreement there is a destination genuinely serving another object, and it still fails the attempt
    /// outright with the non-retryable <c>TargetCorrupt</c> it always gave. Routing all five to a re-observation
    /// instead would let a wrong length or a wrong content hash keep the attempt going and, if the destination then
    /// settled, be committed as a verified placement.</para>
    /// </summary>
    private async Task<Verification> VerifyRevivalAsync(StorageRuntimeDriverLease driverLease, string objectKey, ValidTransfer input, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaximumObservationAttempts; attempt++)
        {
            var observed = await ObserveAsync(driverLease, objectKey, input, null, cancellationToken).ConfigureAwait(false);
            if (observed != null) return observed;
        }

        return new Verification(null, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true));
    }

    /// <summary>
    /// One head-read-hash observation, or null when the object moved out from under it. The move has two faces and
    /// they are one fence: the provider refusing the pinned read, and — for a driver that accepted the pin without
    /// enforcing it, which the conformance kit does not forbid — the metadata it hands back disagreeing with the
    /// HEAD's.
    ///
    /// <para>What null MEANS is the caller's, and the two callers give it opposite answers on purpose:
    /// <see cref="VerifyRevivalAsync"/> takes the observation again, because a second reviver rewriting identical
    /// bytes is what produced it; <see cref="VerifyAsync"/> answers <c>TargetCorrupt</c>, which is the verdict this
    /// fence has always given there. NOT because no such writer can reach an ordinary transfer — one can, and
    /// <see cref="VerifyAsync"/> traces how — but because that caller cannot afford the retryable verdict this loop
    /// gives up with, and the refusal it answers instead clears on its own next generation.</para>
    /// </summary>
    private async Task<Verification?> ObserveAsync(StorageRuntimeDriverLease driverLease, string objectKey, ValidTransfer input, LeaseRenewal? renewal, CancellationToken cancellationToken)
    {
        var driver = driverLease.Driver;
        if (!await RenewIfLeasedAsync(renewal, input.Timeout, cancellationToken).ConfigureAwait(false))
            return new Verification(null, Problem(ArtifactCasProblemCode.StaleWorker, true));
        var head = await InvokeAsync(token => driver.HeadAsync(new ArtifactStorageHeadRequest(objectKey), token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (head.Problem != null) return new Verification(null, head.Problem);
        if (head.Timeout) return new Verification(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (head.Value?.Error != null) return new Verification(null, Map(head.Value.Error, readMissing: true));
        if (!HeadCanMatch(objectKey, input, head.Value!.Metadata!)) return new Verification(null, Problem(ArtifactCasProblemCode.TargetCorrupt));

        if (!await RenewIfLeasedAsync(renewal, input.Timeout, cancellationToken).ConfigureAwait(false))
            return new Verification(null, Problem(ArtifactCasProblemCode.StaleWorker, true));
        var read = await InvokeAsync(token => driver.OpenReadAsync(new ArtifactStorageReadRequest(objectKey)
        {
            ExpectedETag = head.Value.Metadata!.ETag,
            ExpectedVersion = head.Value.Metadata.Version,
        }, token), input.Timeout, cancellationToken, driverLease).ConfigureAwait(false);
        if (read.Problem != null) return new Verification(null, read.Problem);
        if (read.Timeout) return new Verification(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (read.Value?.Error != null)
            return read.Value.Error.Code == ArtifactStorageErrorCode.ConditionNotMet ? null : new Verification(null, Map(read.Value.Error, readMissing: true));

        var content = read.Value!.Content!;
        driverLease.Own(content);
        if (read.Value.ContentLength != input.Size || read.Value.TotalLength != input.Size)
            return new Verification(null, Problem(ArtifactCasProblemCode.TargetCorrupt));
        if (!ContentAgrees(head.Value.Metadata!, read.Value.Metadata!, objectKey))
            return new Verification(null, Problem(ArtifactCasProblemCode.TargetCorrupt));
        if (!ProviderTokensAgree(head.Value.Metadata!, read.Value.Metadata!)) return null;

        if (!await RenewIfLeasedAsync(renewal, input.Timeout, cancellationToken).ConfigureAwait(false))
            return new Verification(null, Problem(ArtifactCasProblemCode.StaleWorker, true));
        var observed = await HashAsync(content, driverLease, input.Timeout, cancellationToken).ConfigureAwait(false);
        if (observed.Problem != null) return new Verification(null, observed.Problem);
        if (observed.Timeout) return new Verification(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        if (observed.Size != input.Size || !CryptographicOperations.FixedTimeEquals(observed.Digest!, input.Digest))
            return new Verification(null, Problem(ArtifactCasProblemCode.TargetCorrupt));
        return new Verification(head.Value.Metadata, null);
    }

    private async Task<ArtifactCasTransferResult> CommitAsync(IntentSnapshot claim, Guid actorId, ArtifactStorageObjectMetadata metadata, LocationFence? fence, CancellationToken cancellationToken)
    {
        const int maximumCommitAttempts = 5;
        for (var attempt = 0; attempt < maximumCommitAttempts; attempt++)
        {
            await using var db = CreateDb();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var intent = await db.ArtifactTransferIntent.SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
                if (intent.State == ArtifactTransferState.Committed)
                    return new ArtifactCasTransferResult.Committed(intent.Id, intent.ArtifactObjectId!.Value, intent.ArtifactLocationId!.Value, true);
                var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
                if (intent.State != ArtifactTransferState.Verifying || !LeaseIsCurrent(intent, claim.Fence, now))
                    return Stale(claim.Id);

                var artifact = await db.ArtifactObject.SingleOrDefaultAsync(value => value.TeamId == claim.TeamId && value.DigestAlgorithm == ArtifactDigestAlgorithm.Sha256 && value.Digest == claim.Digest, cancellationToken).ConfigureAwait(false);
                if (artifact != null && artifact.SizeBytes != claim.Size)
                    return await RollbackAndRejectAsync(transaction, claim, actorId, ArtifactCasProblemCode.TargetCorrupt, cancellationToken).ConfigureAwait(false);
                if (artifact == null)
                {
                    artifact = new ArtifactObject
                    {
                        Id = Guid.NewGuid(), TeamId = claim.TeamId, DigestAlgorithm = ArtifactDigestAlgorithm.Sha256,
                        Digest = claim.Digest, SizeBytes = claim.Size, CreatedDate = now, CreatedBy = actorId,
                    };
                    db.ArtifactObject.Add(artifact);
                }

                var location = await db.ArtifactLocation.SingleOrDefaultAsync(value => value.TeamId == claim.TeamId && value.StorageProfileRevisionId == claim.ProfileRevisionId && value.ObjectKey == claim.ObjectKey, cancellationToken).ConfigureAwait(false);
                if (location != null && !Reusable(location, artifact, claim, fence))
                    return await RollbackAndRejectAsync(transaction, claim, actorId, ArtifactCasProblemCode.IdempotencyConflict, cancellationToken).ConfigureAwait(false);
                if (location == null)
                {
                    location = new ArtifactLocation
                    {
                        Id = Guid.NewGuid(), TeamId = claim.TeamId, ArtifactObjectId = artifact.Id, StorageProfileRevisionId = claim.ProfileRevisionId,
                        Locator = claim.Locator, ObjectKey = claim.ObjectKey, ProviderObjectVersion = metadata.Version, ProviderETag = metadata.ETag,
                        ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = claim.Digest, ObservedSizeBytes = claim.Size,
                        State = ArtifactLocationState.Available, Revision = 1, VerifiedAt = now,
                        CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
                    };
                    db.ArtifactLocation.Add(location);
                    db.ArtifactLocationEvent.Add(Event(location, actorId));
                }
                else
                {
                    Observe(location, claim, metadata, actorId, now);
                    db.ArtifactLocationEvent.Add(Event(location, actorId));
                }

                intent.State = ArtifactTransferState.Committed;
                intent.Revision++;
                intent.ArtifactObjectId = artifact.Id;
                intent.ArtifactLocationId = location.Id;
                intent.CompletedAt = now;
                intent.WorkerLeaseExpiresAt = null;
                intent.LastErrorCode = null;
                intent.LastErrorMessage = null;
                intent.NextAttemptAt = null;
                intent.LastModifiedDate = intent.CompletedAt.Value;
                intent.LastModifiedBy = actorId;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ArtifactCasTransferResult.Committed(intent.Id, artifact.Id, location.Id, false);
            }
            catch (Exception exception) when (IsUniqueViolation(exception) || exception is DbUpdateConcurrencyException)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var raceOutcome = await ReadCommitRaceOutcomeAsync(claim, cancellationToken).ConfigureAwait(false);
                if (raceOutcome != null) return raceOutcome;
                if (attempt == maximumCommitAttempts - 1)
                    return await HandleProblemAsync(claim, actorId, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true), cancellationToken).ConfigureAwait(false);
            }
        }

        return Stale(claim.Id);
    }

    /// <summary>
    /// A location xmin collision does not imply this transfer lost ownership: distinct valid intents may reverify the
    /// same CAS target concurrently. Null means the caller still owns a live Verifying intent and should retry.
    /// </summary>
    private async Task<ArtifactCasTransferResult?> ReadCommitRaceOutcomeAsync(IntentSnapshot claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
        if (intent.State == ArtifactTransferState.Committed)
            return new ArtifactCasTransferResult.Committed(intent.Id, intent.ArtifactObjectId!.Value, intent.ArtifactLocationId!.Value, true);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        return intent.State == ArtifactTransferState.Verifying && LeaseIsCurrent(intent, claim.Fence, now) ? null : Stale(claim.Id);
    }

    private async Task<ArtifactCasTransferResult> RollbackAndRejectAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, IntentSnapshot claim, Guid actorId, ArtifactCasProblemCode code, CancellationToken cancellationToken)
    {
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return await HandleProblemAsync(claim, actorId, Problem(code), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records what went wrong and either parks the transfer on a backoff or closes it, on the DATABASE's clock.
    ///
    /// <para><c>next_attempt_at</c> is the one timestamp this ledger writes that is later JUDGED rather than merely
    /// displayed: <see cref="ClaimAsync"/> compares it against <c>clock_timestamp()</c>, and the recovery sweep
    /// selects and orders on that same clock. Stamping it from this pod's wall clock would make that comparison a
    /// cross-clock one on a deployment where the pod that writes a deadline is routinely not the pod that reads it —
    /// a writer running behind would record a wait that is already over, and one running ahead a wait no reader can
    /// see the end of. The delay is a DURATION and so carries no clock of its own; only its anchor has to be the
    /// database's, and the reading this method already took for the lease check is exactly that anchor.</para>
    ///
    /// <para><c>completed_at</c> is anchored there for a second, sharper reason: it is not merely judged later, it is
    /// judged against another column of the same row. <c>ck_artifact_transfer_intent_revision</c> (0127) demands
    /// <c>completed_at &gt;= created_date</c>, and this method can close a transfer milliseconds after
    /// <see cref="EnsureIntentAsync"/> minted it — so the two have to be read from ONE clock or a pod running ahead
    /// makes the write that records a failure fail. Every timestamp this class ASSIGNS to the row is the database's
    /// for that reason: <c>created_date</c>, <c>completed_at</c> and <c>next_attempt_at</c> from a
    /// <c>clock_timestamp()</c> reading, <c>worker_lease_expires_at</c> computed in SQL.</para>
    ///
    /// <para>One column is this pod's nonetheless, and knowing which is where the boundary actually runs:
    /// <c>last_modified_date</c>. <c>CodeSpaceDbContext</c>'s auditing pass overwrites it from
    /// <c>DateTimeOffset.UtcNow</c> on every EF UPDATE, discarding whatever this class assigned. The INSERT is
    /// spared, because for an added row that pass fills the column only when it is still unset and
    /// <see cref="EnsureIntentAsync"/> has already set it from the same reading as the rest of the row. So the
    /// column holds the database's instant on three paths — the mint, and the raw claim and lease-renewal
    /// statements below which set it in SQL — and this pod's on every EF update after them. That is survivable only because nothing judges it: no CHECK, trigger,
    /// index or sweep on <c>artifact_transfer_intent</c> reads <c>last_modified_date</c>. A reader that wanted to
    /// would have to move the column onto the database's clock first.</para>
    ///
    /// <para>The audit behind that boundary also found a cross-clock comparison on <c>artifact_location</c>:
    /// 0127's <c>ck_artifact_location_observation</c> compared this class's database-stamped <c>created_date</c> with
    /// the later verifier pod's <c>TimeProvider</c>-stamped <c>verified_at</c>. Migration 0185 removes only that invalid
    /// ordering while preserving every intrinsic observation check. The ledger's revision and matching append-only
    /// event establish causality; moving an honest observation forward to agree with another machine's clock would
    /// fabricate when the destination answered.</para>
    /// </summary>
    private async Task<ArtifactCasTransferResult> HandleProblemAsync(IntentSnapshot claim, Guid actorId, ArtifactCasProblem problem, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var intent = await db.ArtifactTransferIntent.SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (!LeaseIsCurrent(intent, claim.Fence, now)) return Stale(claim.Id);
        if (intent.State == ArtifactTransferState.Committed)
            return new ArtifactCasTransferResult.Committed(intent.Id, intent.ArtifactObjectId!.Value, intent.ArtifactLocationId!.Value, true);

        intent.Revision++;
        intent.LastErrorCode = problem.Code.ToString();
        intent.LastErrorMessage = SafeMessage(problem.Code);
        intent.LastModifiedDate = now;
        intent.LastModifiedBy = actorId;
        if (problem.IsRetryable && intent.State is ArtifactTransferState.Intended or ArtifactTransferState.Uploading or ArtifactTransferState.Uploaded or ArtifactTransferState.Verifying)
        {
            intent.State = ArtifactTransferState.RetryScheduled;
            intent.WorkerLeaseExpiresAt = null;
            intent.RetryCount++;
            intent.NextAttemptAt = now + RetryDelay(intent.RetryCount);
        }
        else
        {
            intent.State = ArtifactTransferState.Failed;
            intent.WorkerLeaseExpiresAt = null;
            intent.NextAttemptAt = null;
            intent.CompletedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Stale(claim.Id);
        }

        return intent.State == ArtifactTransferState.RetryScheduled
            ? new ArtifactCasTransferResult.Deferred(intent.Id, intent.NextAttemptAt!.Value, problem)
            : new ArtifactCasTransferResult.Rejected(intent.Id, problem);
    }

    /// <summary>
    /// Mints the durable intent, or hands back the one this idempotency key already names.
    ///
    /// <para>Its birth instant is the DATABASE's, like every other timestamp on the row. That is not a preference:
    /// <c>ck_artifact_transfer_intent_revision</c> (0127) compares <c>completed_at &gt;= created_date</c> directly, and
    /// a non-retryable failure stamps <c>completed_at</c> from the database within milliseconds of this insert — so a
    /// pod running even slightly ahead would write a birth its own death precedes and the settling write would be
    /// rejected outright. Reading it here costs the one round trip that makes the whole row answer to a single clock.</para>
    /// </summary>
    private async Task<IntentSnapshot> EnsureIntentAsync(ArtifactCasTransferRequest request, Guid profileRevisionId, byte[] digest, CancellationToken cancellationToken)
    {
        var key = await IdempotencyKeyAsync(request, profileRevisionId, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var db = CreateDb();
            var existing = await db.ArtifactTransferIntent.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == request.TeamId && value.StorageProfileRevisionId == profileRevisionId && value.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
            if (existing != null) return await LedgerSnapshotAsync(existing, request, digest, cancellationToken).ConfigureAwait(false);

            var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
            var intent = new ArtifactTransferIntent
            {
                Id = Guid.NewGuid(), TeamId = request.TeamId, StorageProfileRevisionId = profileRevisionId,
                IdempotencyKey = key, ExpectedDigestAlgorithm = ArtifactDigestAlgorithm.Sha256,
                ExpectedDigest = digest, ExpectedSizeBytes = request.ExpectedSizeBytes, TargetLocator = request.TargetObjectKey,
                TargetObjectKey = request.TargetObjectKey, State = ArtifactTransferState.Intended, Revision = 1,
                ExecutionAttemptId = request.ExecutionIdentity?.AttemptId, ExecutionAttemptOrdinal = request.ExecutionIdentity?.AttemptOrdinal,
                ExecutionGeneration = request.ExecutionIdentity?.Generation, RetryCount = 0,
                CreatedDate = now, CreatedBy = request.ActorId, LastModifiedDate = now, LastModifiedBy = request.ActorId,
            };
            db.ArtifactTransferIntent.Add(intent);
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return Snapshot(intent, null);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                // The idempotency winner is re-read in a fresh context; a failed context is never reused.
            }
        }

        await using var finalDb = CreateDb();
        var winner = await finalDb.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.TeamId == request.TeamId && value.StorageProfileRevisionId == profileRevisionId && value.IdempotencyKey == key, cancellationToken).ConfigureAwait(false);
        return await LedgerSnapshotAsync(winner, request, digest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The snapshot for an intent this key already names, carrying what the ledger decided about it: a refusal, a re-drivable placement, or neither.</summary>
    private async Task<IntentSnapshot> LedgerSnapshotAsync(ArtifactTransferIntent intent, ArtifactCasTransferRequest request, byte[] digest, CancellationToken cancellationToken)
    {
        var verdict = await LedgerVerdictAsync(intent, request, digest, cancellationToken).ConfigureAwait(false);

        return Snapshot(intent, verdict.Problem) with { Revive = verdict.Revive };
    }

    /// <summary>
    /// Whether the intent this key already names may satisfy the request, and if not, why. Content identity is the
    /// first question. The second exists only for an already-<c>Committed</c> intent, and that is the dedup hit: the
    /// short-circuit that satisfies a write from a stored object without touching the provider.
    ///
    /// <para>A <c>Committed</c> intent is a permanent record that the bytes were once verified at its
    /// <c>artifact_location_id</c>. It is NOT a claim that they are still there and can never become one, because
    /// <c>Committed</c> is a one-way door: <c>artifact_cas_transfer_guard</c> (0131) whitelists no transition out of
    /// it and <c>ck_artifact_transfer_intent_outcome</c> (0127) pins it to that object and location. So liveness is
    /// asked of the location, where it is answerable — <c>Available</c> is the only state whose own
    /// <c>ck_artifact_location_observation</c> demands a verified size and a matching Sha256, which is exactly "these
    /// bytes were observed present here". Every other state, including the <c>Deleting</c> a purge claims a location
    /// with before it removes anything, means not proven present, so the write is refused instead of being satisfied
    /// with an object whose reads would fail.</para>
    ///
    /// <para>Cost: one indexed point read, only on a dedup hit. A first write of new content pays nothing and no path
    /// gains a provider round-trip. Deliberately not a provider head: the answer has to be right for a purge that is
    /// still mid-flight, and only the database knows that.</para>
    ///
    /// <para>The retention reaper now exercises this fence: its short physical claim moves the location to
    /// <c>Deleting</c> before provider I/O, so an overlapping writer gets a typed refusal instead of the id whose bytes
    /// are being removed.</para>
    ///
    /// <para>Refusal is not the answer for a placement that merely LOST its bytes, and a producer cannot route around
    /// this to ask again: the intent scope is derived from the content, so every re-presentation of those exact bytes
    /// arrives on this same committed intent. A <see cref="RevivableState"/> placement is therefore handed back as
    /// re-drivable rather than refused, fenced on the (state, revision) read HERE — before any provider I/O, which is
    /// the only reading a fence may be taken at. Every other non-<c>Available</c> state, the <c>Deleting</c> claim
    /// above most of all, keeps the refusal and this key keeps returning it.</para>
    ///
    /// <para><c>Purged</c> normally never reaches that arm: <see cref="IdempotencyKeyAsync"/> has already spent its
    /// generation, so this key names a fresh intent instead. It stays in the one list anyway, because a purge landing
    /// between those two reads would otherwise fall off both — and the revival taken there is the same write onto the
    /// same row the fresh generation would have made.</para>
    /// </summary>
    private async Task<LedgerVerdict> LedgerVerdictAsync(ArtifactTransferIntent intent, ArtifactCasTransferRequest request, byte[] digest, CancellationToken cancellationToken)
    {
        if (!Matches(intent, request, digest)) return new LedgerVerdict(Problem(ArtifactCasProblemCode.IdempotencyConflict), null);
        if (intent.State != ArtifactTransferState.Committed) return new LedgerVerdict(null, null);

        await using var db = CreateDb();
        var fence = await db.ArtifactLocation.AsNoTracking()
            .Where(value => value.TeamId == intent.TeamId && value.Id == intent.ArtifactLocationId)
            .Select(value => new LocationFence(value.State, value.Revision))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (fence?.State == ArtifactLocationState.Available) return new LedgerVerdict(null, null);

        return fence != null && RevivableState(fence.State)
            ? new LedgerVerdict(null, fence)
            : new LedgerVerdict(Problem(ArtifactCasProblemCode.TargetMissing), null);
    }

    /// <summary>
    /// The intent key for THIS attempt: the caller's scope, plus the generation of the newest intent minted under it
    /// for this exact profile revision — stepped by one when that intent can no longer satisfy a write of its content.
    ///
    /// <para>Two things spend a generation. A <c>Failed</c> intent, which a non-retryable problem drove there. And a
    /// <c>Committed</c> intent whose location has been <c>Purged</c>: its record that the bytes were verified is
    /// permanent and true, but they were intentionally removed since, and re-uploading them is the repair. Every OTHER
    /// non-<c>Available</c> location keeps this key — a mid-purge one because the refusal IS its answer, and a
    /// <c>Missing</c> or <c>Corrupt</c> one because its repair is <see cref="ReviveAsync"/>, which re-drives the very
    /// intent this key names rather than minting another. Widening this to every non-<c>Available</c> state instead
    /// would burn a generation per attempt for as long as a destination stays broken, and would make the spend
    /// non-monotone the moment one of those placements came back.</para>
    ///
    /// <para>Newest-generation-and-step, rather than counting the spent ones, because the count is not monotonic: a
    /// purged location that gets written again is <c>Available</c> once more, which UN-spends its generation. A count
    /// would then fall back onto a key it had already burned and hand every later writer that dead intent's verdict.
    /// The newest generation only ever grows.</para>
    ///
    /// <para>The generation exists because <c>Failed</c> is a one-way door in the database, not merely in the code.
    /// <c>artifact_cas_transfer_guard</c> (0131_artifact_transfer_fence_claim.sql) refuses every route back out of it:
    /// a fence claim raises <c>'terminal rows cannot be claimed'</c> when <c>OLD.state IN ('Committed','Failed',
    /// 'Cancelled')</c>; a plain transition first demands <c>'saga transition requires an unexpired worker lease'</c>,
    /// which a Failed row can never satisfy because the same trigger forbids a terminal row from holding one; and the
    /// transition whitelist has no arm whose <c>OLD.state</c> is <c>'Failed'</c>. So the intent cannot move backwards
    /// — the repaired attempt has to be a NEW intent, and only a distinct idempotency key can mint one under
    /// <c>ux_artifact_transfer_intent_idempotency (team_id, storage_profile_revision_id, idempotency_key)</c>.</para>
    ///
    /// <para>Repairing what broke the transfer is exactly what does NOT bump <c>storage_profile_revision</c> — a
    /// restored credential, a remounted volume, a bucket policy fix all leave the profile revision untouched — so
    /// without this the first write under a misconfiguration would ban those exact bytes under that scope forever.
    /// <c>TargetObjectKey</c> is deliberately NOT generation-aware: every generation targets the same object, so a
    /// retry that finds it already there is provider-side dedup, not a duplicate upload.</para>
    ///
    /// <para>The match is the scope's own key or a <c>/g</c>-suffixed one rather than any prefix of the scope, so
    /// scopes that end in a number — a log stream's segment ordinal, say — cannot step each other's generations:
    /// <c>…/1</c> never reads <c>…/10</c>.</para>
    ///
    /// <para><c>Cancelled</c> is deliberately not stepped over: it is an explicit stop rather than a fault, and
    /// nothing in this codebase produces it today.</para>
    /// </summary>
    private async Task<string> IdempotencyKeyAsync(ArtifactCasTransferRequest request, Guid profileRevisionId, CancellationToken cancellationToken)
    {
        var generationPrefix = $"{request.IdempotencyScope}/g";
        await using var db = CreateDb();
        var minted = await db.ArtifactTransferIntent.AsNoTracking()
            .Where(value => value.TeamId == request.TeamId && value.StorageProfileRevisionId == profileRevisionId
                && (value.IdempotencyKey == request.IdempotencyScope || value.IdempotencyKey.StartsWith(generationPrefix)))
            .Select(value => new MintedIntent(value.IdempotencyKey, value.State, db.ArtifactLocation
                .Where(location => location.TeamId == value.TeamId && location.Id == value.ArtifactLocationId)
                .Select(location => (ArtifactLocationState?)location.State).FirstOrDefault()))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var newest = minted.MaxBy(value => GenerationOf(value.Key, generationPrefix));
        if (newest == null) return request.IdempotencyScope;

        var generation = GenerationOf(newest.Key, generationPrefix);
        return Spent(newest) ? IdempotencyKeyFor(request.IdempotencyScope, generation + 1) : newest.Key;
    }

    /// <summary>Which generation a minted key names: the bare scope is 0, and a <c>/g</c>-suffixed key carries its own.</summary>
    private static int GenerationOf(string key, string generationPrefix) =>
        key.StartsWith(generationPrefix, StringComparison.Ordinal) && int.TryParse(key.AsSpan(generationPrefix.Length), out var generation) ? generation : 0;

    /// <summary>Whether this generation's intent can no longer satisfy a write of its content, so the next attempt needs a fresh one.</summary>
    private static bool Spent(MintedIntent intent) =>
        intent.State == ArtifactTransferState.Failed
        || (intent.State == ArtifactTransferState.Committed && intent.LocationState == ArtifactLocationState.Purged);

    /// <summary>
    /// One attempt generation's intent key. Generation 0 is the bare scope, so the shared-intent behaviour every
    /// concurrent writer depends on is the default, a healthy destination never mints a second key, and keys already
    /// committed before generations existed are still found.
    /// </summary>
    internal static string IdempotencyKeyFor(string scope, int generation) => generation == 0 ? scope : $"{scope}/g{generation}";

    /// <summary>
    /// Takes the fenced worker claim, on a lease and a backoff judged by ONE clock — the database's.
    ///
    /// <para>Every timestamp this statement judges is anchored on <c>clock_timestamp()</c>: the lease by this UPDATE
    /// and by <see cref="RenewLeaseAsync"/>, which compute it in SQL; the backoff by <see cref="HandleProblemAsync"/>,
    /// which adds its delay to a <c>clock_timestamp()</c> reading taken from the database rather than to a local now
    /// — so a recorded wait is short by one round trip, and never by a pod's drift. The recovery sweep selects and
    /// orders its batch on that same clock. Reading any single clause against this pod's wall clock instead makes a
    /// claim answer two questions from two clocks: on a multi-node deployment a pod running behind would refuse a
    /// backoff the database says is over — WITHOUT writing anything, so the row keeps the head of the sweep's
    /// lease-ordered batch and starves every intent queued behind it — and a pod running ahead would jump a wait that
    /// has not elapsed.</para>
    ///
    /// <para>Because this is the only judge, a refusal is also the only place the reason for one can be told apart,
    /// which is what <see cref="ClaimResult.DatabaseNow"/> is read for.</para>
    /// </summary>
    private async Task<ClaimResult> ClaimAsync(Guid teamId, Guid intentId, Guid actorId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var leaseMilliseconds = (long)Math.Ceiling(LeaseDuration(timeout).TotalMilliseconds);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE artifact_transfer_intent
            SET worker_fence_epoch = COALESCE(worker_fence_epoch, 0) + 1,
                worker_lease_expires_at = clock_timestamp() + ({{leaseMilliseconds}} * INTERVAL '1 millisecond'),
                revision = revision + 1,
                last_modified_date = clock_timestamp(),
                last_modified_by = {{actorId}}
            WHERE team_id = {{teamId}} AND id = {{intentId}}
              AND state IN ('Intended', 'Uploading', 'Uploaded', 'Verifying', 'RetryScheduled')
              AND (state <> 'RetryScheduled' OR next_attempt_at <= clock_timestamp())
              AND (worker_lease_expires_at IS NULL OR worker_lease_expires_at <= clock_timestamp())
            """, cancellationToken).ConfigureAwait(false);
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.TeamId == teamId && value.Id == intentId, cancellationToken).ConfigureAwait(false);
        if (affected == 1) return new ClaimResult(Snapshot(intent, null), true, null);

        return new ClaimResult(Snapshot(intent, null), false, await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Why the database refused this claim, in the caller's terms, decided on the clock the refusal was made by.
    ///
    /// <para>The two reasons need different answers and only the database can tell them apart. A backoff still
    /// running is a wait the caller may poll out, and its own deadline is when to come back. Anything else is another
    /// worker holding a live lease, and the caller is told to come back when THAT lapses — telling it to come back at
    /// a backoff instant already in the past would spin it.</para>
    /// </summary>
    private ArtifactCasTransferResult Refused(ClaimResult claimed)
    {
        var claim = claimed.Intent;
        var now = claimed.DatabaseNow!.Value;

        return claim.State == ArtifactTransferState.RetryScheduled && claim.NextAttemptAt > now
            ? new ArtifactCasTransferResult.Deferred(claim.Id, claim.NextAttemptAt.Value, StoredProblem(claim))
            : new ArtifactCasTransferResult.Deferred(claim.Id, claim.LeaseExpiresAt ?? now, Problem(ArtifactCasProblemCode.TransferInProgress, true));
    }

    /// <summary>Keeps a lease alive when there is one to keep. A revival re-drives a terminally committed intent, which holds no lease and can never take one — what fences it is the placement, not a worker claim.</summary>
    private async Task<bool> RenewIfLeasedAsync(LeaseRenewal? renewal, TimeSpan timeout, CancellationToken cancellationToken) =>
        renewal == null || await RenewLeaseAsync(renewal.Claim, renewal.ActorId, timeout, cancellationToken).ConfigureAwait(false);

    private async Task<bool> RenewLeaseAsync(IntentSnapshot claim, Guid actorId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var leaseMilliseconds = (long)Math.Ceiling(LeaseDuration(timeout).TotalMilliseconds);
        var affected = await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE artifact_transfer_intent
            SET worker_lease_expires_at = clock_timestamp() + ({{leaseMilliseconds}} * INTERVAL '1 millisecond'),
                revision = revision + 1,
                last_modified_date = clock_timestamp(),
                last_modified_by = {{actorId}}
            WHERE team_id = {{claim.TeamId}} AND id = {{claim.Id}} AND worker_fence_epoch = {{claim.Fence}}
              AND state IN ('Intended', 'Uploading', 'Uploaded', 'Verifying', 'RetryScheduled')
              AND worker_lease_expires_at > clock_timestamp()
              AND worker_lease_expires_at < clock_timestamp() + ({{leaseMilliseconds}} * INTERVAL '1 millisecond')
            """, cancellationToken).ConfigureAwait(false);
        if (affected == 1) return true;
        var intent = await db.ArtifactTransferIntent.AsNoTracking().SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        return LeaseIsCurrent(intent, claim.Fence, now) && intent.State is not (ArtifactTransferState.Committed or ArtifactTransferState.Failed or ArtifactTransferState.Cancelled);
    }

    private async Task<IntentSnapshot> TransitionAsync(IntentSnapshot claim, ArtifactTransferState state, Guid actorId, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var intent = await db.ArtifactTransferIntent.SingleAsync(value => value.TeamId == claim.TeamId && value.Id == claim.Id, cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (!LeaseIsCurrent(intent, claim.Fence, now)) return claim with { IsStale = true };
        intent.State = state;
        intent.Revision++;
        intent.NextAttemptAt = null;
        intent.LastErrorCode = null;
        intent.LastErrorMessage = null;
        intent.LastModifiedDate = now;
        intent.LastModifiedBy = actorId;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Snapshot(intent, null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return claim with { IsStale = true };
        }
    }

    private async Task<ResolvedProfileRevision> ResolveProfileRevisionAsync(Guid teamId, Guid profileId, int profileRevision, StorageProfileEligibility eligibility, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var row = await (from profile in db.StorageProfile.AsNoTracking()
                         join revision in db.StorageProfileRevision.AsNoTracking().Where(value => value.Revision == profileRevision)
                             on new { profile.TeamId, StorageProfileId = profile.Id } equals new { revision.TeamId, revision.StorageProfileId } into revisions
                         from revision in revisions.DefaultIfEmpty()
                         where profile.TeamId == teamId && profile.Id == profileId
                         select new ProfileRevisionRow(profile.State, revision == null ? null : revision.Id))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row == null) return new ResolvedProfileRevision(null, Problem(ArtifactCasProblemCode.ProfileMissing));
        if (!StorageProfileRules.Admits(row.State, eligibility)) return new ResolvedProfileRevision(null, Problem(ArtifactCasProblemCode.ProfileNotActive));
        return row.RevisionId == null
            ? new ResolvedProfileRevision(null, Problem(ArtifactCasProblemCode.ProfileRevisionMissing))
            : new ResolvedProfileRevision(row.RevisionId, null);
    }

    private async Task<DriverCreation> OpenDriverAsync(DriverActivationRequest request, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(request.Timeout);
        Task<StorageRuntimeDriverResolution>? pending = null;
        try
        {
            pending = _driverBroker.OpenAsync(new StorageRuntimeDriverRequest(request.TeamId, request.ProfileId, request.ProfileRevision, request.Eligibility), timeoutSource.Token).AsTask();
            var resolution = await pending.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            if (resolution is not StorageRuntimeDriverResolution.Ready ready)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timeoutSource.IsCancellationRequested)
                    return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
                return new DriverCreation(null, MapBrokerFailure(resolution));
            }
            if (cancellationToken.IsCancellationRequested)
            {
                await DisposeLeaseQuietlyAsync(ready.Lease).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (timeoutSource.IsCancellationRequested)
            {
                await DisposeLeaseQuietlyAsync(ready.Lease).ConfigureAwait(false);
                return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
            }
            var capabilityProblem = await RequireCapabilitiesAsync(ready.Lease, request.RequiredCapabilities).ConfigureAwait(false);
            return capabilityProblem == null ? new DriverCreation(ready.Lease, null) : new DriverCreation(null, capabilityProblem);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ObserveLateBrokerResolution(pending);
            return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
        }
        catch (OperationCanceledException)
        {
            ObserveLateBrokerResolution(pending);
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            ObserveLateBrokerResolution(pending);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeoutSource.IsCancellationRequested)
                return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderTimeout, true));
            return new DriverCreation(null, Problem(ArtifactCasProblemCode.ProviderFailure, true));
        }
    }

    internal static async Task<ArtifactCasProblem?> RequireCapabilitiesAsync(StorageRuntimeDriverLease lease, StorageProviderCapabilities requiredCapabilities)
    {
        StorageProviderCapabilities capabilities;
        try { capabilities = lease.Driver.Capabilities; }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            await DisposeLeaseQuietlyAsync(lease).ConfigureAwait(false);
            return Problem(ArtifactCasProblemCode.ProviderFailure, true);
        }
        if ((capabilities & requiredCapabilities) == requiredCapabilities) return null;
        await DisposeLeaseQuietlyAsync(lease).ConfigureAwait(false);
        return Problem(ArtifactCasProblemCode.Unsupported);
    }

    private async Task<HashObservation> HashAsync(Stream stream, StorageRuntimeDriverLease driverLease, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        // A dedicated bounded buffer is intentional: a provider stream may ignore cancellation and complete a timed
        // out read later. Pooling would let that late write corrupt another request's re-rented buffer.
        var buffer = new byte[HashBufferSize];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        while (true)
        {
            int read;
            Task<int>? pending = null;
            var abandoned = false;
            try
            {
                pending = driverLease.Track(stream.ReadAsync(buffer.AsMemory(), timeoutSource.Token).AsTask());
                read = await pending.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
                return new HashObservation(null, 0, true, null);
            }
            catch (OperationCanceledException)
            {
                if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
                throw;
            }
            catch (UnauthorizedAccessException)
            {
                return new HashObservation(null, 0, false, Problem(ArtifactCasProblemCode.Forbidden));
            }
            catch (IOException)
            {
                return new HashObservation(null, 0, false, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true));
            }
            catch (Exception)
            {
                return new HashObservation(null, 0, false, Problem(ArtifactCasProblemCode.ProviderFailure, true));
            }
            finally
            {
                if (pending != null && !abandoned) driverLease.Release(pending);
            }
            if (read == 0) return new HashObservation(hash.GetHashAndReset(), size, false, null);
            hash.AppendData(buffer, 0, read);
            size += read;
        }
    }

    private static async Task<Invocation<T>> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, TimeSpan timeout, CancellationToken cancellationToken, StorageRuntimeDriverLease driverLease)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<T>? pending = null;
        var abandoned = false;
        try
        {
            pending = driverLease.Track(action(timeoutSource.Token).AsTask());
            return new Invocation<T>(await pending.WaitAsync(timeoutSource.Token).ConfigureAwait(false), false, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
            return new Invocation<T>(default, true, null);
        }
        catch (OperationCanceledException)
        {
            if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.Forbidden));
        }
        catch (NotSupportedException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.Unsupported));
        }
        catch (InvalidDataException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.TargetCorrupt));
        }
        catch (IOException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true));
        }
        catch (Exception)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.ProviderFailure, true));
        }
        finally
        {
            if (pending != null && !abandoned) driverLease.Release(pending);
        }
    }

    /// <summary>
    /// A write consumes caller-owned bytes. After a timeout/cancellation signal we therefore wait for the provider
    /// task to settle before returning, so a non-conforming plugin cannot continue touching a stream the caller may
    /// now dispose. Qualified drivers settle promptly when their cancellation token is signalled.
    /// </summary>
    private static async Task<Invocation<T>> InvokeOwnedInputAsync<T>(Func<CancellationToken, ValueTask<T>> action, TimeSpan timeout, CancellationToken cancellationToken, StorageRuntimeDriverLease driverLease)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<T>? pending = null;
        var abandoned = false;
        try
        {
            pending = driverLease.Track(action(timeoutSource.Token).AsTask());
            return new Invocation<T>(await pending.WaitAsync(timeoutSource.Token).ConfigureAwait(false), false, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
            await ObserveOwnedInputSettlementAsync(pending).ConfigureAwait(false);
            return new Invocation<T>(default, true, null);
        }
        catch (OperationCanceledException)
        {
            if (pending != null) { abandoned = true; driverLease.Abandon(pending); }
            await ObserveOwnedInputSettlementAsync(pending).ConfigureAwait(false);
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.Forbidden));
        }
        catch (NotSupportedException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.Unsupported));
        }
        catch (InvalidDataException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.TargetCorrupt));
        }
        catch (IOException)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true));
        }
        catch (Exception)
        {
            return new Invocation<T>(default, false, Problem(ArtifactCasProblemCode.ProviderFailure, true));
        }
        finally
        {
            if (pending != null && !abandoned) driverLease.Release(pending);
        }
    }

    private static async Task ObserveOwnedInputSettlementAsync<T>(Task<T>? pending)
    {
        if (pending == null) return;
        try { await pending.ConfigureAwait(false); }
        catch { /* Provider outcome is represented by the timeout/cancellation; never log provider exception text. */ }
    }

    private static ValidTransfer Validate(ArtifactCasTransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty || request.StorageProfileId == Guid.Empty || request.ActorId == Guid.Empty)
            throw new ArgumentException("Team, storage profile and actor ids are required.", nameof(request));
        if (request.StorageProfileRevision <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A positive profile revision is required.");
        if (string.IsNullOrWhiteSpace(request.IdempotencyScope) || request.IdempotencyScope.Length > ArtifactCasTransferRequest.MaximumScopeLength)
            throw new ArgumentException($"A 1-{ArtifactCasTransferRequest.MaximumScopeLength} character idempotency scope is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.TargetObjectKey) || request.TargetObjectKey.Length > 2048)
            throw new ArgumentException("A 1-2048 character target object key is required.", nameof(request));
        if (request.Content == null || !request.Content.CanRead) throw new ArgumentException("A readable content stream is required.", nameof(request));
        if (request.ExpectedSizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(request), "Expected size cannot be negative.");
        if (!TryDigest(request.ExpectedSha256, out var digest)) throw new ArgumentException("ExpectedSha256 must be exactly 64 hexadecimal characters.", nameof(request));
        Validate(request.ExecutionIdentity);
        return new ValidTransfer(digest, request.ExpectedSizeBytes, ValidateTimeout(request.OperationTimeout));
    }

    private static TimeSpan Validate(ArtifactCasReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TeamId == Guid.Empty || request.ArtifactObjectId == Guid.Empty || request.StorageProfileId == Guid.Empty)
            throw new ArgumentException("Team, artifact object and storage profile ids are required.", nameof(request));
        if (request.StorageProfileRevision <= 0) throw new ArgumentOutOfRangeException(nameof(request), "A positive profile revision is required.");
        return ValidateTimeout(request.OperationTimeout);
    }

    private static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? DefaultOperationTimeout;
        if (value <= TimeSpan.Zero || value > MaximumOperationTimeout)
            throw new ArgumentOutOfRangeException(nameof(timeout), $"Operation timeout must be positive and no greater than {MaximumOperationTimeout}.");
        return value;
    }

    private static void Validate(ArtifactCasExecutionIdentity? identity)
    {
        if (identity == null) return;
        if (identity.AttemptId == Guid.Empty || identity.AttemptOrdinal <= 0 || identity.Generation <= 0)
            throw new ArgumentException("Execution identity requires a non-empty attempt and positive ordinal/generation.", nameof(identity));
    }

    private static bool Matches(ArtifactTransferIntent intent, ArtifactCasTransferRequest request, byte[] digest) =>
        intent.ExpectedDigestAlgorithm == ArtifactDigestAlgorithm.Sha256 && intent.ExpectedDigest.AsSpan().SequenceEqual(digest)
        && intent.ExpectedSizeBytes == request.ExpectedSizeBytes && string.Equals(intent.TargetLocator, request.TargetObjectKey, StringComparison.Ordinal)
        && string.Equals(intent.TargetObjectKey, request.TargetObjectKey, StringComparison.Ordinal)
        && intent.ExecutionAttemptId == request.ExecutionIdentity?.AttemptId && intent.ExecutionAttemptOrdinal == request.ExecutionIdentity?.AttemptOrdinal
        && intent.ExecutionGeneration == request.ExecutionIdentity?.Generation;

    /// <summary>
    /// Whether the location row already at this object key is the one this verified transfer may write its observation
    /// onto. Identity comes first and is never waived: the row must bind the same object and the same locator, both of
    /// which 0127's trigger holds immutable for the row's whole life.
    ///
    /// <para>This is the ORDINARY transfer's reader of <see cref="Revivable"/> — a revival commits through
    /// <see cref="ReviveLocationAsync"/> and never arrives here — and widening that whitelist changes what an ordinary
    /// write is answered with. The trace, step by step, because it is a shape that is easy to talk yourself out of:
    /// <see cref="LocationFenceAsync"/> keys the fence on the object KEY rather than on a committed intent's location
    /// id, and nothing binds a producer's idempotency scope to the key it writes. So a producer whose scope names no
    /// intent yet mints a fresh one, carrying no <c>Revive</c>, and drives the plain upload-verify-commit
    /// (<see cref="DriveTransferAsync"/>) onto a key another producer's committed placement already holds. Let that
    /// placement have LOST its bytes since: the fence reads <c>Missing</c> or <c>Corrupt</c>, the HEAD finds the key
    /// empty — or already holding this exact content, the only other thing a create-only path proceeds on — the upload
    /// fills it, the readback verifies it, and this predicate decides the commit.</para>
    ///
    /// <para>Its verdict there MOVED, and moving it is the change rather than a casualty of it. That write used to be
    /// refused <c>IdempotencyConflict</c>, non-retryably: the row was not <c>Available</c>, so <see cref="Verified"/>
    /// said no, and not <c>Purged</c>, so the whitelist said no as well. The refusal was permanent for the content as
    /// well — <see cref="Spent"/> burns the generation that failure closes, the next attempt mints another intent,
    /// drives the identical transfer against the identical fence and is refused identically, for as long as the
    /// placement stays lost. It now commits, onto the row its own readback just proved good.
    /// <c>An_ordinary_write_onto_a_lost_placement_commits_it_back_instead_of_being_refused_forever</c> drives both
    /// lost states here and pins the changed verdict, so it cannot move back in silence.</para>
    /// </summary>
    private static bool Reusable(ArtifactLocation location, ArtifactObject artifact, IntentSnapshot claim, LocationFence? fence) =>
        location.ArtifactObjectId == artifact.Id && string.Equals(location.Locator, claim.Locator, StringComparison.Ordinal)
        && (Verified(location, claim) || Revivable(location, fence));

    /// <summary>
    /// Writes THIS readback onto an existing placement and puts it back in service. Shared by the re-verify a normal
    /// commit does and by a revival, because those two must record the identical observation: the size and checksum
    /// are what <c>ck_artifact_location_observation</c> and 0150's trigger demand of an <c>Available</c> row, so a
    /// revival that merely stepped over a <c>Corrupt</c> row's disagreeing size would be refused by the database.
    /// </summary>
    private static void Observe(ArtifactLocation location, IntentSnapshot claim, ArtifactStorageObjectMetadata metadata, Guid actorId, DateTimeOffset now)
    {
        // A successful readback is a new observation even when the CAS bytes/location already exist.
        // Refresh provider conditions so future reads never pin a superseded ETag/version.
        location.ProviderObjectVersion = metadata.Version;
        location.ProviderETag = metadata.ETag;
        location.ProviderChecksumAlgorithm = "Sha256";
        location.ProviderChecksum = claim.Digest;
        location.ObservedSizeBytes = claim.Size;
        location.State = ArtifactLocationState.Available;
        location.Revision++;
        location.VerifiedAt = now;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.LastModifiedDate = now;
        location.LastModifiedBy = actorId;
    }

    /// <summary>The row already carries a verified observation of exactly this content, so re-verifying it is a refresh.</summary>
    private static bool Verified(ArtifactLocation location, IntentSnapshot claim) =>
        location.State == ArtifactLocationState.Available && location.ObservedSizeBytes == claim.Size
        && string.Equals(location.ProviderChecksumAlgorithm, "Sha256", StringComparison.Ordinal) && location.ProviderChecksum.AsSpan().SequenceEqual(claim.Digest);

    /// <summary>
    /// The row is a placement that lost its bytes and nothing has touched it since this attempt read it, so this
    /// transfer's readback is what it now records. Exactly three states are admitted: <c>Purged</c>, whose bytes were
    /// intentionally removed; <c>Missing</c>, where the destination answered that no object is at the key; and
    /// <c>Corrupt</c>, where it answered with something that is not this object. Each is re-entered on strictly
    /// stronger evidence than the bare HEAD <c>ArtifactLocationVerifier</c> already restores <c>Missing</c> on, since
    /// the commit is only reached once the object has been re-HEADed, re-opened, streamed and re-hashed.
    ///
    /// <para>A WHITELIST, never "any state that is not Available". <c>Deleting</c> is the purge's own claim, taken and
    /// committed before a single byte is removed, and the fence cannot tell it apart: to a writer that arrives after
    /// the claim, state and revision agree at both reads, the HEAD finds the not-yet-deleted object and skips the
    /// upload, the readback passes, and this would publish a freshly verified <c>Available</c> row over bytes the
    /// purge removes moments later — and answer the producer <c>Committed</c>. <c>Deleted</c> is terminal in the
    /// database (0127), and <c>Pending</c> and <c>Failed</c> describe a placement that was never established, so
    /// neither is one to put back.</para>
    ///
    /// <para>The provider observation fields are NOT compared: whatever lost the bytes may legitimately have cleared
    /// the ETag and version it invalidated, and the commit overwrites all of them from THIS readback anyway — the
    /// object binding above is what proves the row is about this content. A row whose revision moved is refused, which
    /// also covers the purge that claimed it while this attempt was mid-flight.</para>
    /// </summary>
    internal static bool Revivable(ArtifactLocation location, LocationFence? fence) =>
        RevivableState(location.State) && fence != null && fence.State == location.State && fence.Revision == location.Revision;

    /// <summary>
    /// The three states above, as the one list both readers of it consult: this commit-time whitelist, and
    /// <see cref="LedgerVerdictAsync"/>, which decides whether a producer re-presenting the content is refused or
    /// allowed to re-drive its intent. Two lists would eventually admit a state to one reader and not the other, and
    /// the pair that must never drift apart is precisely the one this exists to keep out.
    /// </summary>
    private static bool RevivableState(ArtifactLocationState state) =>
        state is ArtifactLocationState.Purged or ArtifactLocationState.Missing or ArtifactLocationState.Corrupt;

    private static bool HeadCanMatch(string objectKey, ValidTransfer input, ArtifactStorageObjectMetadata metadata) =>
        string.Equals(metadata.ObjectKey, objectKey, StringComparison.Ordinal) && metadata.Length == input.Size
        && (metadata.Sha256 == null || string.Equals(metadata.Sha256, Convert.ToHexStringLower(input.Digest), StringComparison.OrdinalIgnoreCase));

    private static bool MetadataMatches(ReadLocation stored, ArtifactStorageObjectMetadata metadata, StorageProviderCapabilities capabilities) =>
        string.Equals(metadata.ObjectKey, stored.ObjectKey, StringComparison.Ordinal) && metadata.Length == stored.Size
        && (metadata.Sha256 == null || string.Equals(metadata.Sha256, Convert.ToHexStringLower(stored.Digest), StringComparison.OrdinalIgnoreCase))
        && (DurableETag(stored.ProviderETag, capabilities) == null || string.Equals(metadata.ETag, stored.ProviderETag, StringComparison.Ordinal))
        && (stored.ProviderObjectVersion == null || string.Equals(metadata.Version, stored.ProviderObjectVersion, StringComparison.Ordinal));

    /// <summary>
    /// A recorded ETag, but only from a provider whose ETag actually identifies the bytes.
    ///
    /// <para>An ETag the destination may change while the object stays put — the local driver derives one from the
    /// file's modification time — is a valid same-session conditional token and a false identity months later. Applied
    /// here rather than at write time so destinations that already recorded such a value stop failing their reads,
    /// which is the state a restore or a migration leaves them in.</para>
    /// </summary>
    internal static string? DurableETag(string? recorded, StorageProviderCapabilities capabilities) =>
        capabilities.HasFlag(StorageProviderCapabilities.StableETag) ? recorded : null;

    /// <summary>
    /// Whether the readback describes the same CONTENT the HEAD that licensed it described. Judged on the three
    /// fields a rewrite of IDENTICAL bytes can never move, so a disagreement here is a destination genuinely serving
    /// another object or a genuine mix-up of two — and every caller answers it with the same non-retryable corruption
    /// verdict it gave before any of this became re-observable.
    ///
    /// <para><c>ObjectKey</c>: both readings must name the key the caller asked for. A rewrite of THIS object cannot
    /// change which key was asked about, so a reading that answers about a different one has confused two objects,
    /// and no repair of either is evidence for that.</para>
    ///
    /// <para><c>Length</c>: a property of the bytes themselves. Writing the same bytes writes the same count, on
    /// every provider, so head-and-open disagreeing about it means the object between them was not the same
    /// object.</para>
    ///
    /// <para><c>Sha256</c>: the content, hashed by the provider — the strongest form of the same argument, and the
    /// only one of the three that is optional, so it is compared only when BOTH readings carry one. A provider that
    /// stopped reporting it has said nothing, and reading silence as disagreement would convict a healthy object.</para>
    /// </summary>
    private static bool ContentAgrees(ArtifactStorageObjectMetadata head, ArtifactStorageObjectMetadata opened, string objectKey) =>
        string.Equals(head.ObjectKey, objectKey, StringComparison.Ordinal) && string.Equals(opened.ObjectKey, objectKey, StringComparison.Ordinal)
        && head.Length == opened.Length
        && (head.Sha256 == null || opened.Sha256 == null || string.Equals(head.Sha256, opened.Sha256, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the two readings still carry the same provider-MINTED tokens. Exactly the two fields the destination
    /// invents for itself rather than deriving from the bytes, and therefore exactly the two an overwrite of
    /// identical content may legitimately move — the local driver derives its ETag from the file's mtime, so it does
    /// there. It is the one shipped destination it moves on: OSS declares <c>StableETag</c>, and a byte-identical
    /// rewrite reproduces a content-derived ETag by definition. Nothing here filters on that, so the comparison has to
    /// be right for both.
    ///
    /// <para>Split from <see cref="ContentAgrees"/> rather than folded in with it because the two carry opposite
    /// meanings. A moved token says the object was rewritten between the two calls and says NOTHING about what was
    /// written, so the answer is to observe again; a moved content field says the destination is holding something
    /// else, and the answer is to refuse. One predicate over all five gave the second answer to both, which is how a
    /// concurrent repair convicts healthy bytes — and, once relaxed, how a genuinely wrong length or hash walks
    /// through as if it were a repair.</para>
    /// </summary>
    private static bool ProviderTokensAgree(ArtifactStorageObjectMetadata head, ArtifactStorageObjectMetadata opened) =>
        string.Equals(head.ETag, opened.ETag, StringComparison.Ordinal) && string.Equals(head.Version, opened.Version, StringComparison.Ordinal);

    /// <summary>
    /// The head-vs-open fence over a stream that is already open, as both read paths apply it: the stream when the
    /// two readings agree, a corruption verdict when they disagree about the CONTENT, and null — take the whole
    /// observation again — when only the provider-minted tokens moved.
    ///
    /// <para>Shared rather than written twice because the two paths must answer a concurrent repair identically. They
    /// pass different length expectations (a whole object against the recorded size, a window against the size it
    /// asked for), so that comparison is made by the caller and arrives here already decided.</para>
    ///
    /// <para>Answering a moved token with another observation rather than a verdict is not free, and what it costs
    /// differs by path because what stands BEHIND this fence differs. On the whole-object path nothing is lost: the
    /// stream handed back is an <see cref="ArtifactCasVerifyingReadStream"/>, which hashes every byte against the
    /// recorded digest, so a swap for a different object of the same length is refused at EOF — unconditionally, and
    /// whether it landed inside the head-to-open window or an hour before it, which this comparison never covered.
    /// Detection there is moved, not removed. On the window path there is no such backstop and the loss is real; it
    /// is named where it lands, on <see cref="DriveRangeAsync"/>.</para>
    /// </summary>
    private static async Task<Invocation<Stream>?> LicensedStreamAsync(ArtifactStorageReadResult opened, ArtifactStorageObjectMetadata head, string objectKey, bool lengthAgrees)
    {
        var content = lengthAgrees && ContentAgrees(head, opened.Metadata!, objectKey);
        if (content && ProviderTokensAgree(head, opened.Metadata!)) return new Invocation<Stream>(opened.Content!, false, null);

        await opened.Content!.DisposeAsync().ConfigureAwait(false);

        return content ? null : new Invocation<Stream>(null, false, Problem(ArtifactCasProblemCode.TargetCorrupt));
    }

    private static bool TryDigest(string value, out byte[] digest)
    {
        digest = [];
        if (value == null || value.Length != 64) return false;
        try
        {
            digest = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static ArtifactLocationEvent Event(ArtifactLocation location, Guid actorId) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
        EventType = ArtifactLocationEventType.Verified, State = location.State, ObservedAt = location.VerifiedAt!.Value,
        ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
        DetailsJson = "{}", CreatedBy = actorId,
    };

    internal static ArtifactCasProblem MapBrokerFailure(StorageRuntimeDriverResolution resolution) => resolution switch
    {
        StorageRuntimeDriverResolution.ProfileUnavailable value => MapProfileFailure(value.Reason),
        StorageRuntimeDriverResolution.CredentialUnavailable value => MapCredentialFailure(value.Reason),
        StorageRuntimeDriverResolution.ProviderUnavailable value => MapProviderFailure(value.Reason),
        StorageRuntimeDriverResolution.ConfigurationInvalid value => MapConfigurationFailure(value.Reason),
        StorageRuntimeDriverResolution.Cancelled value => MapCancellation(value.Stage),
        StorageRuntimeDriverResolution.DriverInitializationFailed value => MapDriverFailure(value.Reason),
        StorageRuntimeDriverResolution.Ready => Problem(ArtifactCasProblemCode.ProviderFailure),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapProfileFailure(StorageRuntimeProfileFailureReason reason) => reason switch
    {
        StorageRuntimeProfileFailureReason.Missing => Problem(ArtifactCasProblemCode.ProfileMissing),
        StorageRuntimeProfileFailureReason.NotActive => Problem(ArtifactCasProblemCode.ProfileNotActive),
        StorageRuntimeProfileFailureReason.RevisionMissing => Problem(ArtifactCasProblemCode.ProfileRevisionMissing),
        StorageRuntimeProfileFailureReason.ResolutionFailed => Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapCredentialFailure(StorageRuntimeCredentialFailureReason reason) => reason switch
    {
        StorageRuntimeCredentialFailureReason.Missing => Problem(ArtifactCasProblemCode.CredentialUnavailable),
        StorageRuntimeCredentialFailureReason.NotActive => Problem(ArtifactCasProblemCode.CredentialUnavailable),
        StorageRuntimeCredentialFailureReason.RevisionMissing => Problem(ArtifactCasProblemCode.CredentialUnavailable),
        StorageRuntimeCredentialFailureReason.ProviderMismatch => Problem(ArtifactCasProblemCode.CredentialInvalid),
        StorageRuntimeCredentialFailureReason.ProviderUnavailable => Problem(ArtifactCasProblemCode.CredentialBrokerUnavailable, true),
        StorageRuntimeCredentialFailureReason.InvalidEnvelope => Problem(ArtifactCasProblemCode.CredentialInvalid),
        StorageRuntimeCredentialFailureReason.InvalidReference => Problem(ArtifactCasProblemCode.CredentialInvalid),
        StorageRuntimeCredentialFailureReason.InvalidSecret => Problem(ArtifactCasProblemCode.CredentialInvalid),
        StorageRuntimeCredentialFailureReason.ResolutionFailed => Problem(ArtifactCasProblemCode.CredentialBrokerUnavailable, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapProviderFailure(StorageRuntimeProviderFailureReason reason) => reason switch
    {
        StorageRuntimeProviderFailureReason.ModuleMissing => Problem(ArtifactCasProblemCode.ProviderUnavailable),
        StorageRuntimeProviderFailureReason.FactoryMissing => Problem(ArtifactCasProblemCode.ProviderUnavailable),
        StorageRuntimeProviderFailureReason.FactoryMismatch => Problem(ArtifactCasProblemCode.ProviderUnavailable),
        StorageRuntimeProviderFailureReason.CatalogFailure => Problem(ArtifactCasProblemCode.ProviderFailure, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapConfigurationFailure(StorageRuntimeConfigurationFailureReason reason) => reason switch
    {
        StorageRuntimeConfigurationFailureReason.InvalidConfiguration => Problem(ArtifactCasProblemCode.ProfileInvalid),
        StorageRuntimeConfigurationFailureReason.UnsupportedSchemaVersion => Problem(ArtifactCasProblemCode.ProfileInvalid),
        StorageRuntimeConfigurationFailureReason.SnapshotIdentityMismatch => Problem(ArtifactCasProblemCode.ProfileInvalid),
        StorageRuntimeConfigurationFailureReason.InvalidProviderTypeKey => Problem(ArtifactCasProblemCode.ProfileInvalid),
        StorageRuntimeConfigurationFailureReason.FactoryRejectedConfiguration => Problem(ArtifactCasProblemCode.Unsupported),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapCancellation(StorageRuntimeCancellationStage stage) => stage switch
    {
        StorageRuntimeCancellationStage.ProfileResolution => Problem(ArtifactCasProblemCode.ProviderTimeout, true),
        StorageRuntimeCancellationStage.CredentialResolution => Problem(ArtifactCasProblemCode.ProviderTimeout, true),
        StorageRuntimeCancellationStage.DriverInitialization => Problem(ArtifactCasProblemCode.ProviderTimeout, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem MapDriverFailure(StorageRuntimeDriverInitializationFailureReason reason) => reason switch
    {
        StorageRuntimeDriverInitializationFailureReason.NullDriver => Problem(ArtifactCasProblemCode.ProviderFailure, true),
        StorageRuntimeDriverInitializationFailureReason.ProviderCanceled => Problem(ArtifactCasProblemCode.ProviderTimeout, true),
        StorageRuntimeDriverInitializationFailureReason.ProviderFailure => Problem(ArtifactCasProblemCode.ProviderFailure, true),
        StorageRuntimeDriverInitializationFailureReason.CleanupFailure => Problem(ArtifactCasProblemCode.ProviderFailure, true),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure),
    };

    private static ArtifactCasProblem Map(ArtifactStorageError error, bool readMissing = false) => error.Code switch
    {
        ArtifactStorageErrorCode.Missing => Problem(readMissing ? ArtifactCasProblemCode.TargetMissing : ArtifactCasProblemCode.TargetMissing, true),
        ArtifactStorageErrorCode.IntegrityMismatch or ArtifactStorageErrorCode.Corrupt => Problem(ArtifactCasProblemCode.TargetCorrupt),
        ArtifactStorageErrorCode.Unauthorized => Problem(ArtifactCasProblemCode.Unauthorized),
        ArtifactStorageErrorCode.Forbidden => Problem(ArtifactCasProblemCode.Forbidden),
        ArtifactStorageErrorCode.Throttled => Problem(ArtifactCasProblemCode.Throttled, true),
        ArtifactStorageErrorCode.Unavailable => Problem(ArtifactCasProblemCode.ProviderUnavailableTransient, true),
        ArtifactStorageErrorCode.Unsupported => Problem(ArtifactCasProblemCode.Unsupported),
        ArtifactStorageErrorCode.ConditionNotMet when readMissing => Problem(ArtifactCasProblemCode.TargetCorrupt),
        ArtifactStorageErrorCode.AlreadyExists or ArtifactStorageErrorCode.ConditionNotMet => Problem(ArtifactCasProblemCode.IdempotencyConflict),
        _ => Problem(ArtifactCasProblemCode.ProviderFailure, error.IsRetryable),
    };

    private static ArtifactCasProblem StoredProblem(IntentSnapshot intent) => Enum.TryParse<ArtifactCasProblemCode>(intent.LastErrorCode, out var code)
        ? Problem(code, intent.State == ArtifactTransferState.RetryScheduled)
        : Problem(ArtifactCasProblemCode.ProviderFailure, intent.State == ArtifactTransferState.RetryScheduled);

    private static ArtifactCasProblem Problem(ArtifactCasProblemCode code, bool retryable = false) => new(code, retryable);
    private static string SafeMessage(ArtifactCasProblemCode code) => $"Artifact CAS transfer stopped with typed outcome '{code}'.";
    private static TimeSpan RetryDelay(int retryCount) => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(retryCount, 8))));
    private static TimeSpan LeaseDuration(TimeSpan timeout) => timeout + (timeout > MinimumLeaseMargin ? timeout : MinimumLeaseMargin);
    private static bool LeaseIsCurrent(ArtifactTransferIntent intent, long? fence, DateTimeOffset now) =>
        fence != null && intent.WorkerFenceEpoch == fence && intent.WorkerLeaseExpiresAt > now;
    private ArtifactCasTransferResult.Deferred Stale(Guid intentId) => new(intentId, _clock.GetUtcNow(), Problem(ArtifactCasProblemCode.StaleWorker, true));
    private static bool IsUniqueViolation(Exception exception) => exception is DbUpdateException { InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } } || exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static void ObserveLateBrokerResolution(Task<StorageRuntimeDriverResolution>? pending)
    {
        if (pending != null) _ = DisposeLateBrokerLeaseAsync(pending);
    }

    private static async Task DisposeLateBrokerLeaseAsync(Task<StorageRuntimeDriverResolution> pending)
    {
        try
        {
            if (await pending.ConfigureAwait(false) is StorageRuntimeDriverResolution.Ready ready)
                await DisposeLeaseQuietlyAsync(ready.Lease).ConfigureAwait(false);
        }
        catch { /* Observe late broker faults without logging provider, configuration or secret material. */ }
    }

    private static async Task DisposeLeaseQuietlyAsync(StorageRuntimeDriverLease lease)
    {
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch { /* Cleanup cannot change the already-typed CAS outcome or expose provider detail. */ }
    }

    private static bool IsRecoverable(Exception exception) => exception is not OutOfMemoryException and not AccessViolationException;

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);

    private static Task<DateTimeOffset> DatabaseClockAsync(CodeSpaceDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);

    private static IntentSnapshot Snapshot(ArtifactTransferIntent intent, ArtifactCasProblem? problem) => new()
    {
        Id = intent.Id,
        TeamId = intent.TeamId,
        ProfileRevisionId = intent.StorageProfileRevisionId,
        State = intent.State,
        Fence = intent.WorkerFenceEpoch,
        Digest = intent.ExpectedDigest,
        Size = intent.ExpectedSizeBytes,
        Locator = intent.TargetLocator,
        ObjectKey = intent.TargetObjectKey,
        ArtifactObjectId = intent.ArtifactObjectId,
        ArtifactLocationId = intent.ArtifactLocationId,
        NextAttemptAt = intent.NextAttemptAt,
        LeaseExpiresAt = intent.WorkerLeaseExpiresAt,
        LastErrorCode = intent.LastErrorCode,
        Problem = problem,
    };

    private sealed record ResolvedProfileRevision(Guid? ProfileRevisionId, ArtifactCasProblem? Problem);
    private sealed record DriverActivationRequest(Guid TeamId, Guid ProfileId, int ProfileRevision, StorageProfileEligibility Eligibility, TimeSpan Timeout, StorageProviderCapabilities RequiredCapabilities);
    private sealed record DriverCreation(StorageRuntimeDriverLease? Lease, ArtifactCasProblem? Problem);
    private sealed record ProfileRevisionRow(StorageProfileState State, Guid? RevisionId);
    /// <summary><c>DatabaseNow</c> is read only when the claim was refused — it is the instant that refusal was made at, and the only clock allowed to say which clause made it.</summary>
    private sealed record ClaimResult(IntentSnapshot Intent, bool Acquired, DateTimeOffset? DatabaseNow);
    private sealed record ValidTransfer(byte[] Digest, long Size, TimeSpan Timeout);
    private sealed record LeaseRenewal(IntentSnapshot Claim, Guid ActorId);
    private sealed record Verification(ArtifactStorageObjectMetadata? Metadata, ArtifactCasProblem? Problem);
    private sealed record Invocation<T>(T? Value, bool Timeout, ArtifactCasProblem? Problem);
    private sealed record HashObservation(byte[]? Digest, long Size, bool Timeout, ArtifactCasProblem? Problem);
    private sealed record ReadLocation(string ObjectKey, string? ProviderETag, string? ProviderObjectVersion, long Size, byte[] Digest);
    /// <summary>Internal (not private) so the whitelist that reads it is unit-pinned directly over every state (InternalsVisibleTo).</summary>
    internal sealed record LocationFence(ArtifactLocationState State, long Revision);
    /// <summary>What the ledger decided about an intent this key already names. At most one side is set: a refusal, or the placement fence a re-drive of that intent is allowed to commit against.</summary>
    private sealed record LedgerVerdict(ArtifactCasProblem? Problem, LocationFence? Revive);
    private sealed record MintedIntent(string Key, ArtifactTransferState State, ArtifactLocationState? LocationState);
    private sealed record IntentSnapshot
    {
        public required Guid Id { get; init; }
        public required Guid TeamId { get; init; }
        public required Guid ProfileRevisionId { get; init; }
        public required ArtifactTransferState State { get; init; }
        public long? Fence { get; init; }
        public required byte[] Digest { get; init; }
        public required long Size { get; init; }
        public required string Locator { get; init; }
        public required string ObjectKey { get; init; }
        public Guid? ArtifactObjectId { get; init; }
        public Guid? ArtifactLocationId { get; init; }
        public DateTimeOffset? NextAttemptAt { get; init; }
        public DateTimeOffset? LeaseExpiresAt { get; init; }
        public string? LastErrorCode { get; init; }
        public ArtifactCasProblem? Problem { get; init; }
        /// <summary>Set only for a committed intent whose placement lost its bytes: the fence, read before any provider I/O, that a re-drive of this intent must still find unmoved at commit.</summary>
        public LocationFence? Revive { get; init; }
        public bool IsStale { get; init; }
    }
}
