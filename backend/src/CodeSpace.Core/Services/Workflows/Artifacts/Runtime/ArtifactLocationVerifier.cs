using System.Data.Common;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// One HEAD per location, and a demotion only on an answer that leaves no room for doubt.
///
/// <para>The batch is the locations owed an answer, a turn at a time from each destination that has any, so a
/// deployment converges on re-checking everything without a schedule anyone has to maintain. Ordering by
/// <c>verified_at</c> also means a destination that keeps answering inconclusively is retried first next pass, because
/// nothing moved its timestamp.</para>
///
/// <para>Every row is read and settled on a connection of its OWN, which is what makes a pass survive a row it cannot
/// write. Postgres aborts the whole transaction block on a constraint violation and refuses every statement after it
/// until that block ends, so one shared connection would turn a single refused row into ninety-nine unasked ones — and
/// this pass is dispatched as a command, inside the transaction <c>TransactionalBehavior</c> opens, so the poison
/// would reach the caller's block too and discard the rows that HAD been settled. Taking a connection per row costs a
/// hundred short-lived contexts an hour and buys the only property that matters here: one row nobody could write
/// costs exactly one row.</para>
/// </summary>
public sealed class ArtifactLocationVerifier : IArtifactLocationVerifier
{
    /// <summary>
    /// How long a location's last answer stands before the batch counts it as owed another one.
    ///
    /// <para>It orders the batch, it does not filter it: a location this recent is taken LAST, never dropped. A bound
    /// that excluded rows would be a coverage ceiling — nothing else schedules this sweep, and <c>verified_at</c> is
    /// its only cursor — so a deployment small enough to be swept in an hour would simply stop being swept.</para>
    ///
    /// <para>Ranked alone, every destination's first row outranks every destination's second, however stale that second
    /// one is. A deployment of many small destinations and one large one therefore spends its batch re-asking rows
    /// answered an hour ago while the large destination advances a single row an hour. Freshness first is what makes
    /// the round robin share what is actually OWED, and it costs the fair pick nothing: a destination that cannot
    /// answer never moves a <c>verified_at</c>, so its rows are never the fresh ones.</para>
    ///
    /// <para>Twelve hours against an hourly pass: long enough that a row just answered for steps aside for the rest of
    /// the day, short enough that a deployment the batch can cover is still covered twice a day.</para>
    /// </summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IStorageRuntimeDriverBroker _broker;
    private readonly TimeProvider _clock;
    private readonly ILogger<ArtifactLocationVerifier> _logger;

    public ArtifactLocationVerifier(DbContextOptions<CodeSpaceDbContext> dbOptions, IStorageRuntimeDriverBroker broker, TimeProvider clock, ILogger<ArtifactLocationVerifier> logger)
    {
        _dbOptions = dbOptions;
        _broker = broker;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ArtifactLocationVerificationSummary> VerifyStaleAsync(int batchSize, CancellationToken cancellationToken)
    {
        var due = await StaleAsync(Math.Clamp(batchSize, 1, 500), cancellationToken).ConfigureAwait(false);
        var unanswered = new UnansweredDestinations();
        var confirmed = 0;
        var restored = 0;
        var missing = 0;
        var corrupt = 0;
        var inconclusive = 0;
        var unrecorded = 0;
        var skipped = 0;

        foreach (var location in due)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (await VerifyOneAsync(location, unanswered, cancellationToken).ConfigureAwait(false))
            {
                case Verdict.Confirmed: confirmed++; break;
                case Verdict.Restored: restored++; break;
                case Verdict.Missing: missing++; break;
                case Verdict.Corrupt: corrupt++; break;
                case Verdict.Unrecorded: unrecorded++; break;
                case Verdict.Skipped: skipped++; break;
                default: inconclusive++; break;
            }
        }

        // One line an operator can read the shape of the failure off: a couple of rows racing a neighbouring pass and
        // every row in the batch failing look identical row by row, and only differ in this ratio.
        if (unrecorded > 0) _logger.LogWarning("A verification pass could not record what it observed of {Unrecorded} of the {Checked} locations it examined", unrecorded, due.Count);

        // And the other shape, which reads the same way: most of a batch behind ONE destination is a single outage,
        // where the same number spread across many is this deployment's whole storage plane going quiet.
        if (skipped > 0) _logger.LogWarning("A verification pass dropped {Skipped} of the {Checked} locations it selected, behind destinations that had already failed to answer in the same pass", skipped, due.Count);

        return new ArtifactLocationVerificationSummary
        {
            Checked = due.Count, Confirmed = confirmed, Restored = restored, Missing = missing, Corrupt = corrupt,
            Inconclusive = inconclusive, Unrecorded = unrecorded, Skipped = skipped,
        };
    }

    /// <summary>
    /// The least recently verified locations that a destination can still speak for.
    ///
    /// <para>Deliberately not filtered by team or profile state: a retired profile still serves its own bytes, so its
    /// locations are exactly as worth verifying. <c>Missing</c> is swept alongside <c>Available</c> because demotion
    /// must not be a one-way door — a destination-wide fault that this verifier mistook for per-object loss has to be
    /// able to correct itself once the destination answers again. <c>Corrupt</c> is NOT swept back: it takes a positive
    /// disagreement to reach, which an outage cannot fabricate, so re-reading it would only risk flapping.</para>
    ///
    /// <para>The two populations get separate shares of the batch rather than competing in one ORDER BY. They grow
    /// independently — an abandoned destination leaves thousands of permanently <c>Missing</c> rows behind, and they
    /// are the OLDEST rows in the table by construction — so a single ordering would spend the entire budget
    /// re-asking about bytes already known to be gone while healthy placements went unchecked. Detection is the
    /// primary job; recovering a wrong demotion is the secondary one, and it gets the smaller share.</para>
    /// </summary>
    private async Task<IReadOnlyList<ArtifactLocation>> StaleAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();

        var recoveryShare = Math.Max(1, batchSize / 4);
        var missing = await OldestAsync(db, ArtifactLocationState.Missing, recoveryShare, cancellationToken).ConfigureAwait(false);
        var available = await OldestAsync(db, ArtifactLocationState.Available, batchSize - missing.Count, cancellationToken).ConfigureAwait(false);

        return [.. available, .. missing];
    }

    /// <summary>
    /// The locations of one state that are owed an answer, taken a turn at a time from every destination that has any.
    ///
    /// <para>Ranking within the destination and ordering by that rank first is what a round robin IS, and the reason it
    /// has to be one is that an unreachable destination's rows are the OLDEST rows in the table by construction: a
    /// destination that stops answering stops moving its <c>verified_at</c>, so a plain ordering hands it more of the
    /// batch the longer it stays down — until it holds all of it and no other destination is examined at all. Ranked,
    /// a destination can occupy the batch only in proportion to how many destinations there are.</para>
    ///
    /// <para>Owed first, THEN a turn each: a location answered for within <see cref="StaleAfter"/> sorts behind every
    /// location that is not, whatever turn it holds. Fairness without that is fairness over rows nobody is waiting on
    /// — a destination whose one row was checked an hour ago holds a first turn, and a first turn outranks another
    /// destination's second turn however many years stale that one is.</para>
    ///
    /// <para>The <c>MATERIALIZED</c> CTE and the join back for the rows themselves are
    /// <c>ArtifactRetentionReaper.ClaimBatchAsync</c>'s shape, with <c>ROW_NUMBER</c> where that query has
    /// <c>DISTINCT ON</c> — a head per destination is one row per destination, and this batch needs many.
    /// <c>xmin</c> is projected explicitly because it is a system column that <c>*</c> does not carry, and without it
    /// every settle would write with no concurrency token at all.</para>
    /// </summary>
    private static async Task<List<ArtifactLocation>> OldestAsync(CodeSpaceDbContext db, ArtifactLocationState state, int take, CancellationToken cancellationToken)
    {
        if (take <= 0) return [];

        var stateName = state.ToString();

        return await db.ArtifactLocation.FromSqlInterpolated($$"""
            WITH ranked AS MATERIALIZED (
                SELECT location.id,
                    (location.verified_at > now() - {{StaleAfter}}) IS TRUE AS fresh,
                    ROW_NUMBER() OVER (PARTITION BY location.team_id, location.storage_profile_revision_id ORDER BY location.verified_at, location.id) AS turn
                FROM artifact_location location
                WHERE location.state = {{stateName}}
            )
            SELECT location.*, location.xmin FROM artifact_location location
            JOIN ranked ON ranked.id = location.id
            ORDER BY ranked.fresh, ranked.turn, location.verified_at, location.id
            LIMIT {{take}}
            """).AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Verdict> VerifyOneAsync(ArtifactLocation location, UnansweredDestinations unanswered, CancellationToken cancellationToken)
    {
        if (unanswered.Contains(location)) return Verdict.Skipped;

        var revision = await RevisionAsync(location, cancellationToken).ConfigureAwait(false);

        if (revision == null) return Verdict.Inconclusive;

        try
        {
            // Read eligibility, never Write: verifying must work against a Disabled or Retired profile, which is
            // precisely where bytes sit longest and rot most quietly.
            var resolution = await _broker.OpenAsync(new StorageRuntimeDriverRequest(location.TeamId, revision.StorageProfileId, revision.Revision, Profiles.StorageProfileEligibility.Read), cancellationToken).ConfigureAwait(false);

            // Filed against the destination because the broker never sees the object key: it refuses on the profile,
            // the credential, the provider or the driver, every one of them a property of the destination that answers
            // identically for every placement pinned to it. Re-asking cannot produce a different answer.
            if (resolution is not StorageRuntimeDriverResolution.Ready ready) return NoAnswer(location, unanswered);

            ArtifactStorageHeadResult head;
            bool destinationLive;

            await using (ready.Lease)
            {
                head = await HeadAsync(ready.Lease.Driver, location, cancellationToken).ConfigureAwait(false);

                // A HEAD that succeeded IS the destination answering, and needs nothing corroborated. Every other
                // answer is asked about — a code and a throw alike, because every one of them is as readily the whole
                // destination's fault as it is this one key's, and only the probe can tell those apart.
                destinationLive = head.IsSuccess || await DestinationAnswersAsync(ready.Lease.Driver, location, cancellationToken).ConfigureAwait(false);

                // Filed HERE, in the scope that established it, and not on the way out. Releasing the lease is itself
                // a call that can raise — a descriptor whose mount was pulled out from under it, a client whose
                // shutdown times out — and it raises AFTER this answer exists and BEFORE anything past this block
                // runs. An answer filed past that point is not filed at all, so the destination this pass has just
                // proved cannot answer would be asked again at a full activation and a round trip on every remaining
                // row it holds, which is the exact cost this containment exists to remove.
                if (!destinationLive) return NoAnswer(location, unanswered);
            }

            // Inside the guard even though the settle carries one of its own: that one answers for the database
            // refusing the write, and this one covers every other way settling a row can fail. Either escaping here
            // would end the batch and take every row behind this one with it.
            return await SettleAsync(location, head, destinationLive, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Files nothing, because nothing arriving here has established anything that is not filed already. Asking
            // about the object and asking the destination about itself each answer for their own throws, so what is
            // left is an activation that produced no driver, a settle failing for something other than the database,
            // and the release of a lease. Only the last of those runs after an answer about the destination exists —
            // and that answer was filed inside the block that produced it, before the release could raise, which is
            // precisely why the filing does not live out here.
            _logger.LogWarning(exception, "Verification of artifact location {LocationId} could not reach its destination; the row and its verified_at are left as they were", location.Id);

            return Verdict.Inconclusive;
        }
    }

    /// <summary>
    /// What the destination says about this row's object — and, when asking it THREW, what an error code would have
    /// said in the same breath: that something went wrong with the request, and nothing whatever about the object.
    ///
    /// <para>A throw is not an exception to the rule that every unsettled answer is corroborated by a probe; it is an
    /// instance of it. A driver that raises on every request is a destination-level fault, one that raises on a single
    /// object is not, and the exception separates those two exactly as poorly as an error code does — which is
    /// precisely the case the probe exists to settle. Letting it escape to the row's own guard skips that probe, and
    /// leaves the destination-wide fault that raises rather than returns costing a full activation and a round trip on
    /// every row it holds, in every pass, forever.</para>
    ///
    /// <para><c>ProviderFailure</c> can never be written down — only a success or <c>Missing</c> is an answer about the
    /// object — so it stands in for the throw only across the two questions asked of it here: whether the object was
    /// found, and whether the destination therefore has to be asked about itself.</para>
    /// </summary>
    private async Task<ArtifactStorageHeadResult> HeadAsync(IArtifactStorageDriver driver, ArtifactLocation location, CancellationToken cancellationToken)
    {
        try
        {
            return await driver.HeadAsync(new ArtifactStorageHeadRequest(location.ObjectKey), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Verification of artifact location {LocationId} raised while asking its destination about the object; the destination is asked about itself instead", location.Id);

            return ArtifactStorageHeadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.ProviderFailure, $"The destination raised while being asked about {location.ObjectKey}.", IsRetryable: true));
        }
    }

    /// <summary>
    /// Files this row's destination as one that did not answer, so the rows behind it are dropped from this pass
    /// rather than buying the same silence again at a round trip each.
    ///
    /// <para>Only the NEGATIVE verdict is ever remembered. THIS row's outcome is still <see cref="Verdict.Inconclusive"/>,
    /// because this row really was asked; its siblings become <see cref="Verdict.Skipped"/>, and the two stay apart so
    /// a pass that met one dead destination cannot read as a pass that met a hundred.</para>
    /// </summary>
    private static Verdict NoAnswer(ArtifactLocation location, UnansweredDestinations unanswered)
    {
        unanswered.Add(location);

        return Verdict.Inconclusive;
    }

    /// <summary>
    /// The profile revision this location was written under, on a connection of its own so nothing stays open across
    /// the provider round trip that follows — or null, when this pass could not find out.
    ///
    /// <para>Guarded HERE rather than by the caller's try, because this read runs BEFORE that try opens and is
    /// therefore the one way out of a row's work the row's own guard cannot close. It is a database call like every
    /// other one, failing for the ordinary reasons: a pool exhausted for a moment, a database that blinked. Unguarded,
    /// one such moment on one row ends the entire pass and takes every row behind it — the exact loss this containment
    /// exists to prevent, surviving one line above the guard that prevents it.</para>
    ///
    /// <para>A refusal here and a revision that is simply not there are the same answer to the caller, and that answer
    /// is <c>Inconclusive</c>. Never <c>Unrecorded</c>: nothing about the object had been observed at this point, so
    /// nothing failed to be written down, and saying otherwise would claim an observation this pass never made.</para>
    /// </summary>
    private async Task<StorageProfileRevision?> RevisionAsync(ArtifactLocation location, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = CreateDb();

            return await db.StorageProfileRevision.AsNoTracking()
                .SingleOrDefaultAsync(value => value.TeamId == location.TeamId && value.Id == location.StorageProfileRevisionId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Verification of artifact location {LocationId} could not read the storage profile revision it was written under; the row and its verified_at are left as they were", location.Id);

            return null;
        }
    }

    /// <summary>
    /// Makes this row's observation durable, all of it or none of it, on a connection nothing else is using — and
    /// answers for the attempt when the database will not take it.
    ///
    /// <para>The transaction is EXPLICIT because a restore is two advances of the row — the confirmation, then the
    /// return to <c>Available</c> — and they can be neither merged nor left independent. They cannot be merged: the
    /// schema's <c>artifact_location_event_guard</c> admits an entry only at the location's current or immediately
    /// next revision, so a single save that moved the row two revisions and appended both entries has the first one
    /// rejected, which is a restore that never happens and a warning per row. They cannot be independent either: a
    /// second write the database refuses would leave the first standing, and the row this pass reports as unrecorded
    /// would nonetheless have had its <c>verified_at</c> — the sweep's own cursor — moved. So: two saves, one
    /// transaction. An outcome that recorded nothing leaves nothing recorded.</para>
    ///
    /// <para>The row is <c>Attach</c>ed rather than re-read: it carries the <c>xmin</c> it was read with, and that is
    /// what makes the write conditional on the row still being the one this pass observed. Re-reading it here would
    /// quietly drop that guard and let a pass overwrite a verdict another writer reached in the meantime.</para>
    ///
    /// <para>The guard belongs HERE and not around the whole row's work, because this is the only scope in which the
    /// two failures are distinguishable. <c>Inconclusive</c> says the DESTINATION could not answer; <c>Unrecorded</c>
    /// says WE could not write down an answer we already had. Everything past the first line of this method runs on an
    /// answer that WILL be written down, so a database failure anywhere inside it is the second — while a guard placed
    /// further out would meet the very same exception types coming from the profile and credential reads the broker
    /// does on its way to opening a driver, and would file those as failures to record something nothing had yet
    /// observed.</para>
    ///
    /// <para>Which is also why that first line comes first, before a context or a transaction exists. A row whose
    /// answer was never about the object writes nothing — so opening one to write nothing can only ever produce a
    /// failure this guard would then file as <c>Unrecorded</c>, reporting that an observation was lost on a row where
    /// none was made. Deciding before writing is what keeps both verdicts meaning exactly what they say.</para>
    /// </summary>
    private async Task<Verdict> SettleAsync(ArtifactLocation location, ArtifactStorageHeadResult head, bool destinationLive, CancellationToken cancellationToken)
    {
        if (!IsAboutTheObject(head, destinationLive)) return Verdict.Inconclusive;

        var previous = location.State;

        try
        {
            await using var db = CreateDb();
            db.ArtifactLocation.Attach(location);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var verdict = await RecordAsync(db, location, head, cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            Announce(location, previous, verdict);

            return verdict;
        }
        catch (Exception exception) when (exception is DbUpdateException or DbException)
        {
            // BOTH families, and both as BASE types. They are disjoint — DbUpdateException does not derive from
            // DbException — and they arrive from different halves of this method: EF raises the first out of
            // SaveChangesAsync, while BeginTransactionAsync and CommitAsync hand the provider's own DbException
            // straight through. Naming only the first would leave a refused COMMIT to the caller's generic arm, and a
            // refused COMMIT is routine here rather than exotic, because this schema's DEFERRED constraints are
            // checked at exactly that moment.
            //
            // The base types matter for a second reason: a pass that lost this row to a second writer usually collides
            // on ux_artifact_location_event_revision — the winner already took the revision this settle appends —
            // which arrives as a plain DbUpdateException and not as the concurrency subtype. Check constraints and
            // integrity triggers arrive the same way. So the ONE thing established here is that the observation could
            // not be written down. The log says that and nothing more; naming a cause would be inventing one.
            _logger.LogWarning(exception, "This pass could not record what it observed of artifact location {LocationId}; the row is left exactly as it was", location.Id);

            return Verdict.Unrecorded;
        }
    }

    /// <summary>
    /// Whether the provider said anything about the OBJECT — the only kind of answer that gets written down, and
    /// therefore the question that has to be settled before anything is opened to write one.
    ///
    /// <para>Exactly two answers are about the object. A successful HEAD is one: it carries the size and hash the
    /// recorded identity is compared against. <c>Missing</c> is the other — the one error code that is a statement
    /// about the object rather than about the moment. Every other error — throttled, unauthorized, unavailable, a
    /// transport fault — says something about the REQUEST, and a location must never be touched for that.</para>
    ///
    /// <para>Even <c>Missing</c> is only believed once the destination has proved it can still answer, because a
    /// provider cannot tell a deleted object apart from a whole namespace it can no longer see: the local driver
    /// resolves a missing object with <c>File.Exists</c>, which is equally false when the mount underneath is gone.
    /// Without that corroboration an unmounted volume would demote every location a team owns in a single pass.</para>
    /// </summary>
    private static bool IsAboutTheObject(ArtifactStorageHeadResult head, bool destinationLive) => head.IsSuccess || (IsObjectMissing(head) && destinationLive);

    /// <summary>
    /// Turns one provider answer about the object into the durable advances it justifies, one revision at a time,
    /// inside the transaction the caller owns. Only ever reached for an answer <see cref="IsAboutTheObject"/> admitted,
    /// so every path out of here has written something.
    ///
    /// <para>A size or hash that disagrees with what was recorded means the key now holds something else, which is not
    /// the artifact whatever it is. This is a HEAD, not a re-hash: silent bit rot inside an object of the right size is
    /// caught by the read path, which verifies the content digest and raises <c>IntegrityFailure</c>. The division is
    /// deliberate — reads prove content, and this proves presence, which is the half no reader can discover until
    /// someone happens to ask.</para>
    /// </summary>
    private async Task<Verdict> RecordAsync(CodeSpaceDbContext db, ArtifactLocation location, ArtifactStorageHeadResult head, CancellationToken cancellationToken)
    {
        if (!head.IsSuccess)
        {
            if (location.State == ArtifactLocationState.Missing)
            {
                await MarkObservedAsync(db, location, cancellationToken).ConfigureAwait(false);

                return Verdict.Missing;
            }

            await DemoteAsync(db, location, ArtifactLocationState.Missing, "location-object-missing", $"The destination reports no object at {location.ObjectKey}.", cancellationToken).ConfigureAwait(false);

            return Verdict.Missing;
        }

        var metadata = head.Metadata!;
        if (Disagrees(location, metadata, out var detail))
        {
            await DemoteAsync(db, location, ArtifactLocationState.Corrupt, "location-object-mismatch", detail, cancellationToken).ConfigureAwait(false);
            return Verdict.Corrupt;
        }

        await ConfirmAsync(db, location, cancellationToken).ConfigureAwait(false);
        if (location.State == ArtifactLocationState.Missing)
        {
            await RestoreAsync(db, location, cancellationToken).ConfigureAwait(false);

            return Verdict.Restored;
        }

        return Verdict.Confirmed;
    }

    /// <summary>Says out loud only what is already durable: this runs after the save, so a write the database refused announces nothing.</summary>
    private void Announce(ArtifactLocation location, ArtifactLocationState previous, Verdict verdict)
    {
        if (verdict == Verdict.Restored)
        {
            _logger.LogInformation("Artifact location {LocationId} answered again at its destination and returned to Available", location.Id);
            return;
        }

        if (location.State == previous) return;

        _logger.LogWarning("Artifact location {LocationId} is no longer serving the object that was recorded ({State}): {Detail}", location.Id, location.State, location.LastErrorMessage);
    }

    /// <summary>
    /// Whether the object at the key is still the one that was recorded. Size is compared always; the ETag only when
    /// BOTH sides have one, because a provider that stopped reporting one has told us nothing, and treating that as a
    /// mismatch would demote a healthy object.
    /// </summary>
    /// <summary>The checksum recorded at write time, as the lowercase hex the drivers report, or null when the provider gave none.</summary>
    private static string? RecordedSha256(ArtifactLocation location) =>
        location.ProviderChecksum is { Length: > 0 } checksum && string.Equals(location.ProviderChecksumAlgorithm, "Sha256", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToHexStringLower(checksum)
            : null;

    private static bool IsObjectMissing(ArtifactStorageHeadResult head) => !head.IsSuccess && head.Error?.Code == ArtifactStorageErrorCode.Missing;

    /// <summary>
    /// Asks whether the destination itself is still answering — so a gone namespace cannot be read as a gone object,
    /// and so a destination that has stopped answering at all is discovered once instead of once per row.
    ///
    /// <para>This is the ONLY answer that says anything about a destination's other placements, and therefore the only
    /// one the pass ever remembers. A HEAD never is: <c>Missing</c>, <c>Forbidden</c>, <c>Unsupported</c>, a throttle
    /// — each of them is equally an expired credential, a revoked role, an unmounted volume that answers the same way
    /// for every key, or one drifted ACL on one object, and nothing in the code the answer carries separates the
    /// two.</para>
    ///
    /// <para>Which is why it is asked of every answer that did not settle the question about the object, and why the
    /// memo is filed on THIS answer rather than on the HEAD that prompted it. Filing a HEAD silences every placement
    /// behind a single refused key — permanently, because a per-object refusal does not heal and a row nothing was
    /// established about never moves its <c>verified_at</c>, so it leads its destination's ranking again next hour.
    /// Not asking at all costs the mirror of that: a destination-wide fault that never says <c>Missing</c> buys the
    /// identical refusal at a full activation and a round trip on every row it holds, in every pass, forever. One
    /// probe per unsettled row is what closes both.</para>
    ///
    /// <para>A probe that THROWS is a destination that did not answer, and is treated as one. It is the same fault
    /// arriving by a different door — the driver a vanished mount leaves behind raises where another returns
    /// <c>Unavailable</c> — and letting it escape instead would put the destination-wide faults that raise back
    /// outside the memo, which is the whole cost above, rebuilt.</para>
    /// </summary>
    private async Task<bool> DestinationAnswersAsync(IArtifactStorageDriver driver, ArtifactLocation location, CancellationToken cancellationToken)
    {
        try
        {
            var probe = await driver.ProbeAsync(new ArtifactStorageProbeRequest(), cancellationToken).ConfigureAwait(false);

            return probe.Status is ArtifactStorageProbeStatus.Available or ArtifactStorageProbeStatus.ReadOnly;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The destination behind artifact location {LocationId} raised when asked whether it is still answering; it is treated as one that is not", location.Id);

            return false;
        }
    }

    /// <summary>
    /// Whether the destination is demonstrably holding something other than the recorded object.
    ///
    /// <para>Only content-derived identity counts. Size always is. A provider-computed hash is, when the provider
    /// returns one. <c>ProviderETag</c> deliberately is NOT compared: an ETag is provider-defined, and the local
    /// driver derives it from the file's mtime — so a restore from backup, a re-upload, or anything that rewrites
    /// identical bytes changes it. Treating that as evidence would mark a whole destination Corrupt the first time it
    /// was recovered, which is the opposite of what a verifier is for.</para>
    /// </summary>
    private static bool Disagrees(ArtifactLocation location, ArtifactStorageObjectMetadata metadata, out string detail)
    {
        if (location.ObservedSizeBytes is { } size && metadata.Length != size)
        {
            detail = $"The destination holds {metadata.Length} bytes at {location.ObjectKey}; {size} were recorded.";
            return true;
        }

        if (RecordedSha256(location) is { } recorded && metadata.Sha256 is { } observed && !string.Equals(recorded, observed, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"The destination hashes the object at {location.ObjectKey} to {observed}; {recorded} was recorded.";
            return true;
        }

        detail = string.Empty;
        return false;
    }

    /// <summary>Advances the row and its append-only event together: the schema requires a byte-identical event snapshot at every revision, and a demotion that could not record itself would be a state nothing explains.</summary>
    private async Task DemoteAsync(CodeSpaceDbContext db, ArtifactLocation location, ArtifactLocationState state, string errorCode, string detail, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.State = state;
        location.Revision++;
        location.VerifiedAt = now;
        location.LastErrorCode = errorCode;
        location.LastErrorMessage = detail;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.StateChanged, now));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Moves verified_at forward and nothing else. The row was already Available; what changes is only WHEN that was last actually known.</summary>
    /// <summary>
    /// Returns a location the destination can serve again to <c>Available</c>, and clears the error it was carrying.
    ///
    /// <para>Only ever reached after a successful HEAD whose size and ETag agree with what was recorded at write time,
    /// so this restores on the same evidence the original placement was accepted on — never on mere reachability.</para>
    /// </summary>
    private async Task RestoreAsync(CodeSpaceDbContext db, ArtifactLocation location, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.State = ArtifactLocationState.Available;
        location.LastErrorCode = null;
        location.LastErrorMessage = null;
        location.Revision++;
        location.VerifiedAt = now;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Verified, now));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records that a location the sweep already knows is <c>Missing</c> was asked again and answered the same way.
    ///
    /// <para>The state does not change, but <c>verified_at</c> does, and that is the entire point: it is also the
    /// sweep's cursor, so a conclusive answer that did not move it would leave the row pinned at the front of the
    /// ordering forever. Enough such rows and the batch is permanently full of them and no healthy placement is ever
    /// examined again. A demotion and a re-confirmation are both answers about the object; only an outcome the
    /// destination could not answer leaves the column alone.</para>
    ///
    /// <para>It is an <c>Observed</c> ledger entry rather than <c>Verified</c>: nothing was verified to be present.
    /// The row's revision advances because the schema requires every observation of a location to be an entry — which
    /// is also why the recovery share of the batch is small, since this writes one event per re-check.</para>
    /// </summary>
    private async Task MarkObservedAsync(CodeSpaceDbContext db, ArtifactLocation location, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.Revision++;
        location.VerifiedAt = now;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Observed, now));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ConfirmAsync(CodeSpaceDbContext db, ArtifactLocation location, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        location.Revision++;
        location.VerifiedAt = now;
        location.LastModifiedDate = now;
        location.LastModifiedBy = Messages.Constants.SystemUsers.SeederId;
        db.ArtifactLocationEvent.Add(Snapshot(location, ArtifactLocationEventType.Verified, now));

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ArtifactLocationEvent Snapshot(ArtifactLocation location, ArtifactLocationEventType eventType, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
        EventType = eventType, State = location.State, ObservedAt = now,
        ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
        ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage, DetailsJson = "{}",
        CreatedBy = Messages.Constants.SystemUsers.SeederId,
    };

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);

    private enum Verdict { Confirmed, Restored, Missing, Corrupt, Inconclusive, Unrecorded, Skipped }
}
