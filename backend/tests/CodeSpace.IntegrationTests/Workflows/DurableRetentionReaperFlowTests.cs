using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.AgentRunLogging;
using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Credentials;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers.Local;
using CodeSpace.Core.Services.Workflows.Artifacts.Retention;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Core.Services.Workflows.Retention;
using CodeSpace.Core.Services.Workflows.Retention.Cursors;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Artifacts;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The durable-retention plane against a real database and a real local-rwx destination, where the parts that cannot
/// be unit-tested actually live: the guard trigger that decides which retention statements PostgreSQL admits, the pin
/// rows a seal writes in its own transaction, and the order in which bytes and their tombstone commit.
///
/// <para>The positive control at the top is what proves the counter-examples below it are not passing vacuously: each
/// of those builds a stream that is ONE property away from collectable and asserts its bytes survive. Every assertion
/// is about a row this class created — never a sweep tally, which is deployment-wide over a shared database and
/// therefore counts other classes' rows as readily as its own.</para>
///
/// <para>The class also reclaims what it staged (<see cref="DisposeAsync"/>): a bounded global sweep elsewhere in the
/// suite — the artifact-location verifier — selects the hundred least-recently-verified placements across every team,
/// so a class that leaves live placements behind a destination it then deletes changes what that sweep sees.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DurableRetentionReaperFlowTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<string> _roots = [];
    private readonly List<StagedStream> _staged = [];

    public DurableRetentionReaperFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The positive control, and the ordering claim with it: the bytes leave the destination FIRST and the head row's
    /// tombstone commits after, so a crash between them leaves bytes gone and a row the next sweep finishes — never a
    /// row that says purged about bytes still being paid for.
    /// </summary>
    [Fact]
    public async Task A_terminal_unpinned_log_stream_past_its_rule_is_purged_blobs_first_then_rows_and_the_Room_reads_purged()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "the archived transcript of a run nobody opened again");
        await AgeAsync(stream, TimeSpan.FromDays(31));

        await SweepAsync();

        var quarantined = await StreamAsync(stream);
        quarantined.PurgedAt.ShouldBeNull("the sweep that first noticed the stream must not touch its bytes");
        quarantined.RetainUntil.ShouldNotBeNull("the quarantine deadline has to be durable — an in-memory one is no wait at all");
        (await BytesReadableAsync(world, stream)).ShouldBeTrue();

        await ElapseQuarantineAsync(stream);
        await SweepAsync();

        var purged = await StreamAsync(stream);
        purged.PurgedAt.ShouldNotBeNull("the head row is the tombstone: it outlives its bytes so a reader is told they were reclaimed");
        purged.State.ShouldBe(AgentRunLogStreamState.Completed, "a purge is not a capture verdict — the state it settled in is untouched");
        purged.TotalBytes.ShouldBeGreaterThan(0, "the byte head still records what was captured; only the bytes themselves are gone");
        (await UnpurgedLocationsAsync(world, stream)).ShouldBe(0, "every location behind the stream's segments must be drained before the tombstone commits");
        RoomProjector.SummarizeLogs([Row(purged)]).Status.ShouldBe(RoomAgentLogStatus.Purged,
            "the Room must say purged rather than claim an integrity proof over bytes that are gone");
    }

    /// <summary>
    /// What a reader is told about a stream whose bytes were reclaimed. A 410 alone is the answer a LOST object gets,
    /// so the code beside it carries the distinction, and the metadata must stop advertising an integrity proof over
    /// bytes that are gone.
    /// </summary>
    [Fact]
    public async Task The_read_path_answers_a_purged_stream_as_purged_and_claims_no_integrity()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "bytes a reader will come looking for");
        var before = await MetadataAsync(world, stream);
        before.Integrity.ShouldNotBeNull().ManifestDigest.ShouldNotBeNull("the positive control: while the bytes are there the stream does carry its proof");
        before.PurgedAt.ShouldBeNull();

        await PurgeAsync(world, stream);

        var read = (await ReadAsync(world, stream)).ShouldBeOfType<AgentRunLogRangeResult.Unavailable>();
        read.Problem.Code.ShouldBe(AgentRunLogProblemCode.Purged,
            "ArtifactMissing means an object that should be there is not; a policy reclamation must never be reported in the vocabulary of data loss");
        var after = await MetadataAsync(world, stream);
        after.PurgedAt.ShouldNotBeNull("a caller that LISTS streams never attempts a read, so the tombstone has to ride on the metadata too");
        after.Integrity.ShouldBeNull("the manifest receipt is still in the row as the record of what WAS verified; projecting it now would claim these bytes are verifiable");
        after.TotalBytes.ShouldBe(before.TotalBytes, "what was captured is still stated; only the claim that it can be read is withdrawn");
    }

    /// <summary>
    /// The pin-table-first property, from the other side: a stream a sealed qualification result cites is never
    /// collected, however long both windows have been open. Mutation: drop the pin probe in
    /// <c>LogStreamRetentionCursor.ClassifyAsync</c> and this test reds with the stream purged.
    /// </summary>
    [Fact]
    public async Task A_pinned_log_stream_past_its_rule_survives()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "evidence a capability claim was computed from");
        await SealResultAsync(world);
        await AgeAsync(stream, TimeSpan.FromDays(400));

        await SweepAsync();

        (await StreamAsync(stream)).RetainUntil.ShouldBeNull("a cited stream is never quarantined — the verdict is keep, not a wait");

        // The strongest form of the claim: a quarantine deadline that has ALREADY elapsed, so the only thing left
        // between the reaper and the bytes is the pin.
        await ElapseQuarantineAsync(stream);
        await SweepAsync();

        var kept = await StreamAsync(stream);
        kept.PurgedAt.ShouldBeNull("a sealed result still cites this stream; no elapsed window outranks that");
        kept.RetainUntil.ShouldBeNull("and the citation cleared the quarantine it contradicts, so an un-pinned stream starts its wait afresh");
        (await BytesReadableAsync(world, stream)).ShouldBeTrue();
        (await UnpurgedLocationsAsync(world, stream)).ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The starvation property. A record nothing can ever reclaim is claimed by an oldest-first query on every tick,
    /// so a keep that wrote nothing would fill the batch with the same rows for ever and the sweep would report a
    /// healthy claim count while collecting nothing. Mutation: stop settling the keeps (or drop the recheck predicate
    /// from the claim query) and the kept stream comes back in the second claim, ahead of the eligible one.
    /// </summary>
    [Fact]
    public async Task A_stream_that_was_kept_is_not_re_claimed_until_its_recheck_interval_elapses()
    {
        var world = await SeedWorldAsync();
        var pinned = await CaptureAsync(world, "evidence pinned for ever");
        await SealResultAsync(world);
        await AgeAsync(pinned, TimeSpan.FromDays(400));
        await SweepAsync();

        // A second team, because the claim is fair per tenant: two streams of ONE team can never be in the same
        // batch, so a single-team version of this test could not tell starvation from that fairness.
        var neighbour = await SeedWorldAsync();
        var collectable = await CaptureAsync(neighbour, "a younger archive behind it");
        await AgeAsync(collectable, TimeSpan.FromDays(31));
        var claimed = await ClaimAsync(limit: 200);

        claimed.ShouldNotContain(pinned, "a stream this sweep already looked at and kept must not be handed back on the next tick — at scale it would own the batch for ever");
        claimed.ShouldContain(collectable, "and the stream behind it, which no sweep has looked at, must be reachable");
    }

    /// <summary>
    /// Throughput, which the per-tenant fairness of the claim query would otherwise cap at one record per tenant per
    /// tick: with an hourly cadence and a two-visit collection that is about a dozen streams a team a day, while a
    /// single run writes two. The loop asks again until the batch is full. Mutation: claim once and only the oldest
    /// stream of the three is ever swept.
    /// </summary>
    [Fact]
    public async Task One_sweep_reaches_every_eligible_stream_of_a_team_not_just_its_oldest()
    {
        var world = await SeedWorldAsync();
        var streams = new List<Guid>();

        foreach (var kind in new[] { AgentRunLogKinds.StandardOutput, AgentRunLogKinds.StandardError, AgentRunLogKinds.Transcript })
        {
            var stream = await CaptureAsync(world, $"archive {kind}", kind);
            await AgeAsync(stream, TimeSpan.FromDays(31));
            streams.Add(stream);
        }

        await SweepAsync();

        foreach (var stream in streams)
            (await StreamAsync(stream)).RetainUntil.ShouldNotBeNull($"stream {stream} of the same team was never reached by the sweep");
    }

    /// <summary>
    /// The atomicity claim, measured rather than asserted: <c>xmin</c> is the id of the transaction that inserted a
    /// row, so a result and its pins sharing one <c>xmin</c> IS "written in the same transaction". Mutation: move the
    /// pin write to its own <c>SaveChanges</c> (or its own scope) and the two ids differ — which is the crash window
    /// in which a sealed result exists whose evidence the reaper is free to reclaim.
    /// </summary>
    [Fact]
    public async Task A_sealed_result_pins_every_stream_and_artifact_it_cites_in_one_transaction()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "one archived stream");
        var (receiptId, artifactId) = await SeedCitedRecordsAsync(world);

        var groupId = await SealResultAsync(world);

        var pins = await PinsAsync(groupId);
        pins.Where(pin => pin.Kind == DurablePinKind.AgentRun).Select(pin => pin.Target).ShouldBe([world.AgentRunId]);
        pins.Where(pin => pin.Kind == DurablePinKind.LogStream).Select(pin => pin.Target).ShouldBe([stream]);
        pins.Where(pin => pin.Kind == DurablePinKind.CleanupReceipt).Select(pin => pin.Target).ShouldBe([receiptId]);
        pins.Where(pin => pin.Kind == DurablePinKind.Artifact).Select(pin => pin.Target).ShouldBe([artifactId]);
        pins.ShouldAllBe(pin => (pin.Kind == DurablePinKind.Artifact) == (pin.PinnedArtifactId != null),
            "an artifact pin has to sit in the column the artifact oracle probes by name, and nothing else may");

        var transactions = await InsertingTransactionsAsync(groupId);
        transactions.Count.ShouldBe(1, $"the result and its {pins.Count} pins must commit together; they were inserted by {transactions.Count} transactions");

        ArtifactReferenceOracle.ReferenceSites.ShouldContain(("paired_qualification_result_pin", "pinned_artifact_id"),
            "an artifact a sealed result pins has to be a probed reference site, or the ARTIFACT reaper collects what this plane is protecting");
    }

    /// <summary>
    /// Migration 0235's backfill and the writer have to agree, because a result sealed before this plane existed would
    /// otherwise read as citing nothing and its evidence would become collectable. Mutation: change either closure
    /// (drop the cleanup receipts from the backfill, add a kind to the writer) and the two sets differ here.
    /// </summary>
    [Fact]
    public async Task The_backfill_writes_exactly_the_pins_the_seal_writes()
    {
        var world = await SeedWorldAsync();
        await CaptureAsync(world, "a stream a pre-existing result cites");
        await SeedCitedRecordsAsync(world);
        var groupId = await SealResultAsync(world);
        var written = (await PinsAsync(groupId)).Select(Identity).ToList();

        written.ShouldNotBeEmpty("the comparison below is vacuous unless the writer actually pinned something");
        await ForgetPinsAsync(groupId);
        await RunBackfillAsync();

        (await PinsAsync(groupId)).Select(Identity).Order(StringComparer.Ordinal)
            .ShouldBe(written.Order(StringComparer.Ordinal),
                "0235's backfill is the same four closures as PairedQualificationResultStore.PinCitedRecordsAsync; if they drift, results sealed before this migration lose the protection new ones get");
    }

    /// <summary>
    /// Bytes before rows, proven by the failure: with the destination refusing, the sweep must leave the head row's
    /// tombstone unwritten. Mutation: stamp the tombstone before (or without) the byte removal and this reds — the
    /// Room would then read "purged" about an archive still sitting at the destination, and no later sweep would ever
    /// reclaim it, because a purged row is never claimed again.
    /// </summary>
    [Fact]
    public async Task A_refused_byte_purge_leaves_the_tombstone_unwritten_and_defers_the_stream()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "bytes at a destination that stops answering");
        await AgeAsync(stream, TimeSpan.FromDays(31));
        await SweepAsync();
        await ElapseQuarantineAsync(stream);
        var beforeRefusal = await StreamAsync(stream);

        await SweepAgainstARefusingDestinationAsync();

        var kept = await StreamAsync(stream);
        kept.PurgedAt.ShouldBeNull("the tombstone must never run ahead of the bytes");
        (await UnpurgedLocationsAsync(world, stream)).ShouldBeGreaterThan(0, "a refused delete leaves the location exactly where it was");
        kept.LastModifiedAt.ShouldBeGreaterThan(beforeRefusal.LastModifiedAt,
            "a refusal is deferred rather than retried every tick, so one unreachable destination cannot own a batch slot");
        (await ClaimAsync(limit: 10)).ShouldNotContain(stream);
    }

    /// <summary>
    /// A drain too big for one sweep must NOT be deferred like a refusal: it is making progress, so it writes nothing
    /// and the next tick continues it. Mutation: defer a partial drain and a large capture takes one recheck interval
    /// per batch of objects — a 4 GiB archive would need months.
    /// </summary>
    [Fact]
    public async Task A_drain_too_big_for_one_sweep_writes_nothing_and_the_next_sweep_finishes_it()
    {
        var world = await SeedWorldAsync();
        var segments = LogStreamRetentionCursor.MaxPlacementsPerSweep + 1;
        var stream = await CaptureAsync(world, "first", AgentRunLogKinds.StandardOutput, segments);
        await AgeAsync(stream, TimeSpan.FromDays(31));
        await SweepAsync();
        await ElapseQuarantineAsync(stream);
        var beforeDrain = await StreamAsync(stream);

        await SweepAsync();

        var partly = await StreamAsync(stream);
        partly.PurgedAt.ShouldBeNull("the tombstone waits until every object is gone");
        partly.LastModifiedAt.ShouldBe(beforeDrain.LastModifiedAt, "a drain that is progressing writes NOTHING, so the very next tick continues it");
        (await UnpurgedLocationsAsync(world, stream)).ShouldBe(segments - LogStreamRetentionCursor.MaxPlacementsPerSweep, "exactly one bounded batch of objects went");

        await SweepAsync();

        (await StreamAsync(stream)).PurgedAt.ShouldNotBeNull("the second sweep finishes the drain and stamps the tombstone");
        (await UnpurgedLocationsAsync(world, stream)).ShouldBe(0);
    }

    /// <summary>
    /// Deduplication INSIDE one stream. Two byte-identical segments are one content-addressed object with a placement
    /// per write, both at the same destination, and nothing else names them — so the stream is collectable and the
    /// drain has to take each placement by name. Mutation: purge by object id alone and the coordinator refuses to
    /// guess which placement, so a capture that repeated itself would never be reclaimed at all.
    /// </summary>
    [Fact]
    public async Task A_stream_that_captured_the_same_bytes_twice_is_still_collected()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "identical", segments: 2, distinctSegments: false);
        (await ObjectsOfAsync(world, stream)).Count.ShouldBe(1, "the premise: the CAS stored ONE object for the two identical segments");
        (await UnpurgedLocationsAsync(world, stream)).ShouldBe(2, "and ONE placement per write, which an unnamed purge claim refuses to choose between");

        await AgeAsync(stream, TimeSpan.FromDays(31));
        await SweepAsync();
        await ElapseQuarantineAsync(stream);
        await SweepAsync();

        (await StreamAsync(stream)).PurgedAt.ShouldNotBeNull("nothing outside this stream names these bytes, so repeating itself must not make a capture unreclaimable");
        (await UnpurgedLocationsAsync(world, stream)).ShouldBe(0, "every placement of the object goes, not just the one an unnamed claim would have found");
    }

    /// <summary>
    /// Two streams that captured identical output are ONE content-addressed object under two names — the ordinary case
    /// for short or empty logs. Mutation: drop the sibling-segment probe and purging either one silently empties the
    /// other, whose head row still says its bytes are there.
    /// </summary>
    [Fact]
    public async Task A_stream_whose_bytes_another_stream_also_names_is_never_collected()
    {
        var world = await SeedWorldAsync();
        var identical = "byte-for-byte the same output";
        var first = await CaptureAsync(world, identical);
        var second = await CaptureAsync(world, identical, AgentRunLogKinds.StandardError);
        (await ObjectsOfAsync(world, first)).ShouldBe(await ObjectsOfAsync(world, second), "the premise: the CAS stored one object for both captures");

        await AgeAsync(first, TimeSpan.FromDays(31));
        await SweepAsync();
        await ElapseQuarantineAsync(first);
        await SweepAsync();

        (await StreamAsync(first)).PurgedAt.ShouldBeNull("another stream still reaches these bytes");
        (await UnpurgedLocationsAsync(world, first)).ShouldBeGreaterThan(0);
        (await BytesReadableAsync(world, second)).ShouldBeTrue("and the stream that was never a candidate still reads");

        // The verdict itself, because the outcome alone does not pin the reason: deduplicated captures also leave one
        // object with a placement per write, which the drain refuses separately as replication. Asserting the
        // CITATION verdict is what reds when the sibling-segment probe is removed.
        var candidate = (await CandidatesAsync(limit: 200)).FirstOrDefault(row => row.Id == first)
            ?? new DurableRetentionCandidate(first, world.TeamId, (await StreamAsync(first)).Revision, DateTimeOffset.UtcNow.AddDays(-99), null);
        (await Cursor().ClassifyAsync(candidate, CancellationToken.None)).ShouldBe(DurableReferenceVerdict.Referenced,
            "the stream is kept because something else NAMES its bytes, not because of how they happen to be placed");
    }

    /// <summary>
    /// Why a transfer intent is NOT one of the citers the cursor probes, asserted rather than reasoned about: the
    /// schema refuses to let a saga in flight name an object at all. Every row that does name one is a finished
    /// transfer — and a transfer is what wrote each log segment — so probing the column would answer "still cited"
    /// about every log stream in the deployment for ever while looking exactly like a safety property.
    /// </summary>
    [Fact]
    public async Task A_transfer_saga_in_flight_cannot_name_the_object_it_is_moving()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "bytes a transfer already moved");
        var objectId = (await ObjectsOfAsync(world, stream)).Single();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.ArtifactTransferIntent.Add(await InFlightIntentAsync(db, world, objectId));

        var raised = await Should.ThrowAsync<Exception>(async () => await db.SaveChangesAsync());

        PostgresErrorOf(raised).MessageText.ShouldContain("ck_artifact_transfer_intent_outcome", customMessage:
            "if a non-terminal intent could ever carry an artifact_object_id, it WOULD be a citer and the cursor would have to probe it");
        (await db.ArtifactTransferIntent.AsNoTracking().CountAsync(intent => intent.ArtifactObjectId == objectId && intent.State != ArtifactTransferState.Committed))
            .ShouldBe(0, "the only rows naming this object are the committed transfer that wrote it");
    }

    /// <summary>
    /// The age floor, at the CALL SITE rather than in the pure decision: the window the loop computes from the class's
    /// rule is what keeps a young stream out of the batch entirely. Mutation: widen the window (or drop the
    /// completed_at predicate) and a stream one day short of its floor is claimed.
    /// </summary>
    [Fact]
    public async Task A_stream_inside_its_age_floor_is_never_claimed()
    {
        var world = await SeedWorldAsync();
        var young = await CaptureAsync(world, "a capture from last month, one day short");
        await AgeAsync(young, DurableRetentionPolicy.LogStream.MinimumAge - TimeSpan.FromDays(1));

        (await ClaimAsync(limit: 50)).ShouldNotContain(young);

        await AgeAsync(young, TimeSpan.FromDays(2));

        (await ClaimAsync(limit: 50)).ShouldContain(young, "the counter-example is only worth anything if the same stream IS claimed once its floor has passed");
    }

    /// <summary>
    /// The claim query requires a completion instant because the age floor is measured from one. That predicate can
    /// never be the ONLY thing standing between a clockless row and a purge, and this is why: the schema refuses to
    /// hold a terminal stream without one at all. Asserting the refusal rather than fabricating the row keeps the
    /// guarantee where it actually lives.
    /// </summary>
    [Fact]
    public async Task A_terminal_stream_cannot_exist_without_the_instant_its_floor_is_measured_from()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "a settled capture");
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE agent_run_log_stream DISABLE TRIGGER agent_run_log_stream_enforce_invariants");

        try
        {
            var raised = await Should.ThrowAsync<Exception>(async () =>
                await db.Database.ExecuteSqlRawAsync("UPDATE agent_run_log_stream SET completed_at = NULL WHERE id = {0}", [stream]));

            PostgresErrorOf(raised).MessageText.ShouldContain("ck_agent_run_log_stream_terminal");
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE agent_run_log_stream ENABLE TRIGGER agent_run_log_stream_enforce_invariants");
        }
    }

    /// <summary>
    /// The fence. A settlement carries the revision its claim saw, and a stream another worker moved in between must
    /// take nothing from it. Mutation: drop the revision predicate from the stamp and two workers racing one stream
    /// both write, so the loser's stale verdict lands on top of the winner's.
    /// </summary>
    [Fact]
    public async Task A_settlement_whose_revision_moved_writes_nothing()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "a stream two workers both claimed");
        await AgeAsync(stream, TimeSpan.FromDays(31));
        var claimed = (await CandidatesAsync(limit: 50)).Single(candidate => candidate.Id == stream);
        await ElapseQuarantineAsync(stream);   // somebody else moved the row after this claim was taken
        var moved = await StreamAsync(stream);

        var settled = await Cursor().SettleAsync(claimed, DurableRetentionDecision.Quarantine(DateTimeOffset.UtcNow.AddDays(1)), CancellationToken.None);

        settled.ShouldBeFalse("the claim is stale, so it settles nothing");
        var after = await StreamAsync(stream);
        after.Revision.ShouldBe(moved.Revision, "and it must leave the row exactly where the other worker put it");
        after.RetainUntil.ShouldBe(moved.RetainUntil);
    }

    /// <summary>
    /// The distinction the tombstone exists for. A stream whose bytes were lost by some OTHER path must not be stamped
    /// "purged", or the Room would tell the operator a retention window elapsed on an archive nobody reclaimed.
    /// Mutation: treat an empty reclaimable set as drained and this reds with a tombstone on a loss this plane did not
    /// cause.
    /// </summary>
    [Fact]
    public async Task A_stream_whose_bytes_vanished_outside_this_plane_is_never_tombstoned()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "bytes something else lost");
        await AgeAsync(stream, TimeSpan.FromDays(31));
        await SweepAsync();
        await ElapseQuarantineAsync(stream);
        await MarkLocationsDeletedAsync(world, stream);

        await SweepAsync();

        var kept = await StreamAsync(stream);
        kept.PurgedAt.ShouldBeNull("this plane removed nothing, so it may not report a purge");
        RoomProjector.SummarizeLogs([Row(kept)]).Status.ShouldNotBe(RoomAgentLogStatus.Purged,
            "\"purged; retention window elapsed\" about a loss nobody chose inverts the one distinction the tombstone preserves");
    }

    /// <summary>
    /// Migration 0236's arm, at the only place that can pin it: the database. A terminal stream admits a retention
    /// statement and NOTHING else — a statement that smuggles anything alongside the two columns reads the same
    /// refusal it always did.
    /// </summary>
    [Theory]
    [InlineData("retain_until = now(), state = 'Corrupt'", "terminal state is immutable")]
    [InlineData("retain_until = now(), total_bytes = 0", "terminal state is immutable")]
    [InlineData("retain_until = now(), completed_at = now()", "terminal state is immutable")]
    [InlineData("state = 'Open', completed_at = NULL", "terminal state is immutable")]
    [InlineData("purged_at = now()", "cannot be purged without the retain_until")]
    [InlineData("purged_at = now(), retain_until = now() - interval '1 day'", "cannot be purged without the retain_until")]
    public async Task The_guard_refuses_a_retention_statement_that_says_more_than_retention(string smuggled, string refusal)
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "a settled stream");
        using var scope = _fixture.BeginScope();
        var sql = $"UPDATE agent_run_log_stream SET {smuggled}, revision = revision + 1, last_modified_at = now() WHERE team_id = {{0}} AND id = {{1}}";

        var raised = await Should.ThrowAsync<Exception>(async () =>
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(sql, [world.TeamId, stream]));

        PostgresErrorOf(raised).MessageText.ShouldContain(refusal);
        var row = await StreamAsync(stream);
        row.RetainUntil.ShouldBeNull("a refused statement leaves the row exactly as it was");
        row.PurgedAt.ShouldBeNull();
    }

    /// <summary>A live capture is never a retention candidate, and the guard is what makes that true rather than a query predicate somebody can forget.</summary>
    [Fact]
    public async Task The_guard_refuses_a_retention_statement_on_an_open_stream()
    {
        var world = await SeedWorldAsync();
        using var scope = _fixture.BeginScope();
        var logs = Logs(scope);
        var session = Guid.NewGuid();
        var opened = (await logs.OpenAsync(Open(world, session), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();

        var raised = await Should.ThrowAsync<Exception>(async () => await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE agent_run_log_stream SET retain_until = now(), revision = revision + 1, last_modified_at = now() WHERE team_id = {0} AND id = {1}",
            [world.TeamId, opened.Metadata.StreamId]));

        PostgresErrorOf(raised).MessageText.ShouldContain("rejected on a live stream");
    }

    /// <summary>A purge is final: nothing may put a stream back into a state where it claims to hold bytes it gave up.</summary>
    [Fact]
    public async Task The_guard_refuses_to_un_purge_a_purged_stream()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "a stream that will be reclaimed");
        await PurgeAsync(world, stream);

        using var scope = _fixture.BeginScope();
        var raised = await Should.ThrowAsync<Exception>(async () => await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE agent_run_log_stream SET purged_at = NULL, revision = revision + 1, last_modified_at = now() WHERE team_id = {0} AND id = {1}",
            [world.TeamId, stream]));

        PostgresErrorOf(raised).MessageText.ShouldContain("purge is final");
    }

    /// <summary>
    /// Why this plane reclaims bytes and not rows, stated as the refusals themselves rather than as prose in a pull
    /// request: all three archive tables reject deletion outright, so a tombstone on the surviving head is the only
    /// legible way to say the bytes are gone.
    /// </summary>
    [Fact]
    public async Task The_log_archive_tables_refuse_every_deletion()
    {
        var world = await SeedWorldAsync();
        var stream = await CaptureAsync(world, "an archive nothing may delete");
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        foreach (var (table, predicate, refusal) in new[]
        {
            ("agent_run_log_segment", "stream_id", "append-only"),
            ("agent_run_log_verification", "stream_id", "durable history"),
            ("agent_run_log_stream", "id", "DELETE rejected"),
        })
        {
            var raised = await Should.ThrowAsync<Exception>(async () =>
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM {table} WHERE {predicate} = {{0}}", [stream]));

            PostgresErrorOf(raised).MessageText.ShouldContain(refusal, customMessage: $"{table} is supposed to refuse deletion; if it no longer does, this plane could reclaim rows instead of tombstoning them");
        }
    }

    // ── The world, and the durable streams inside it ───────────────────────────────────────────────────────────────

    /// <summary>One real capture through the real service: an open, <paramref name="segments"/> segments at a real local-rwx destination, a finalized source and a v3 completion.</summary>
    private async Task<Guid> CaptureAsync(World world, string text, string kind = AgentRunLogKinds.StandardOutput, int segments = 1, bool distinctSegments = true)
    {
        using var scope = _fixture.BeginScope();
        var logs = Logs(scope);
        var session = Guid.NewGuid();
        var opened = (await logs.OpenAsync(Open(world, session, kind), CancellationToken.None)).ShouldBeOfType<AgentRunLogOpenResult.Opened>();
        var metadata = opened.Metadata;

        for (var ordinal = 1; ordinal <= segments; ordinal++)
        {
            // Distinct bytes per segment by default: the CAS is content-addressed, so repeating a payload makes two
            // segments ONE object with a placement each — which is its own case, and the caller says when it wants it.
            var suffix = distinctSegments ? $" #{ordinal:D4}" : string.Empty;
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{text}{suffix} {new string('x', 24)}");
            metadata = (await logs.AppendAsync(new AgentRunLogAppendRequest
            {
                TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = metadata.StreamId, WorkerFenceEpoch = Fence,
                CaptureSessionId = session, ExpectedSegmentOrdinal = ordinal, ExpectedOffsetBytes = metadata.TotalBytes,
                ExpectedSourceOffsetBytes = metadata.SourceOffsetBytes, SourceLengthBytes = bytes.Length,
                StorageProfileId = world.StorageProfileId, StorageProfileRevision = 1, ActorId = world.ActorId, Bytes = bytes,
            }, CancellationToken.None)).ShouldBeOfType<AgentRunLogAppendResult.Appended>().Metadata;
        }

        var finalized = (await logs.FinalizeSourceAsync(new AgentRunLogFinalizeSourceRequest
        {
            TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = metadata.StreamId, WorkerFenceEpoch = Fence,
            CaptureSessionId = session, ExpectedRevision = metadata.Revision, ExpectedSourceOffsetBytes = metadata.SourceOffsetBytes,
        }, CancellationToken.None)).ShouldBeOfType<AgentRunLogFinalizeSourceResult.Finalized>();
        // Completion verifies a bounded number of segments per call, so a many-segment capture reports Progress until
        // its manifest is whole — exactly what the production bridge does, and what a single call would silently miss.
        var revision = finalized.Metadata.Revision;

        for (var pass = 0; pass < segments + 2; pass++)
        {
            var completion = await logs.CompleteAsync(new AgentRunLogCompleteRequest
            {
                TeamId = world.TeamId, AgentRunId = world.AgentRunId, StreamId = metadata.StreamId, WorkerFenceEpoch = Fence,
                CaptureSessionId = session, ExpectedRevision = revision,
            }, CancellationToken.None);

            if (completion is AgentRunLogCompleteResult.Completed) break;

            revision = completion.ShouldBeOfType<AgentRunLogCompleteResult.Progress>().Metadata.Revision;
        }

        (await StreamAsync(metadata.StreamId)).State.ShouldBe(AgentRunLogStreamState.Completed, "the capture this test starts from has to settle, or every assertion after it is about the wrong row");
        _staged.Add(new StagedStream(world.TeamId, metadata.StreamId));

        return metadata.StreamId;
    }

    /// <summary>
    /// Time travel for the two clocks the claim query reads: how long ago the capture settled, and how long ago
    /// anything last touched the row. Every timestamp on the row moves by the SAME delta, so their ordering — which
    /// <c>ck_agent_run_log_stream_time</c> enforces and which is a real invariant rather than test scaffolding — still
    /// holds; the shift is at least the recheck interval so that "age it by nothing" still reopens the deferral gate.
    ///
    /// <para>The trigger is suspended for this one statement because the guard admits no rewrite of
    /// <c>completed_at</c> at all — which is exactly the property the guard theory pins — and both windows are
    /// measured in days no test can wait out.</para>
    /// </summary>
    private async Task AgeAsync(Guid streamId, TimeSpan age)
    {
        var shift = age > TimeSpan.FromDays(2) ? age : TimeSpan.FromDays(2);
        const string sql = "UPDATE agent_run_log_stream SET created_at = created_at - {0}::interval, capture_finalized_at = capture_finalized_at - {0}::interval, "
            + "completed_at = completed_at - {0}::interval, last_modified_at = last_modified_at - {0}::interval WHERE id = {1}";
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE agent_run_log_stream DISABLE TRIGGER agent_run_log_stream_enforce_invariants");
        try { await db.Database.ExecuteSqlRawAsync(sql, [$"{shift.TotalSeconds} seconds", streamId]); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE agent_run_log_stream ENABLE TRIGGER agent_run_log_stream_enforce_invariants"); }
    }

    /// <summary>
    /// Winds the clock past the quarantine the sweep itself wrote: the deadline lands in the past, and the row looks
    /// settled two days ago rather than a moment ago, so the deferral gate is open again. Both are time travel — the
    /// guard rightly refuses to move a modification time backwards — so it runs with the trigger suspended, exactly
    /// like <see cref="AgeAsync"/>. That the reaper's OWN quarantine write is admitted by the guard is not assumed
    /// here: it is what put a value in <c>retain_until</c> for this helper to move.
    /// </summary>
    private async Task ElapseQuarantineAsync(Guid streamId)
    {
        // GREATEST so the modification time never falls below the clocks ck_agent_run_log_stream_time orders it
        // against: a stream whose capture was aged into the past is already older than the recheck interval anyway.
        const string sql = "UPDATE agent_run_log_stream SET retain_until = now() - interval '1 second', revision = revision + 1, "
            + "last_modified_at = GREATEST(created_at, capture_finalized_at, completed_at, last_modified_at - interval '2 days') WHERE id = {0}";
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE agent_run_log_stream DISABLE TRIGGER agent_run_log_stream_enforce_invariants");

        try { (await db.Database.ExecuteSqlRawAsync(sql, [streamId])).ShouldBe(1); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE agent_run_log_stream ENABLE TRIGGER agent_run_log_stream_enforce_invariants"); }
    }

    /// <summary>Drives one stream all the way through the plane — quarantine, elapse, collect — for the tests whose subject is what happens AFTER a purge.</summary>
    private async Task PurgeAsync(World world, Guid streamId)
    {
        await AgeAsync(streamId, TimeSpan.FromDays(31));
        var quarantine = await SweepAsync();
        await ElapseQuarantineAsync(streamId);
        var collection = await SweepAsync();

        (await StreamAsync(streamId)).PurgedAt.ShouldNotBeNull(
            $"the premise of this test is a stream the plane actually reclaimed; quarantine pass {Describe(quarantine)}, collection pass {Describe(collection)}");
        (await UnpurgedLocationsAsync(world, streamId)).ShouldBe(0);
    }

    /// <summary>
    /// Takes every location behind this stream's objects out of the purge lifecycle without purging it. <c>Deleted</c>
    /// is the state that says so: 0127's trigger makes it terminal, so unlike <c>Missing</c> — which a purge can still
    /// drive to <c>Purged</c> — these bytes left by a path this plane will never drive. Its guards are suspended for
    /// the one statement, exactly as the stream's are for time travel.
    /// </summary>
    private async Task MarkLocationsDeletedAsync(World world, Guid streamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var objects = await ObjectsOfAsync(world, streamId);
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE artifact_location DISABLE TRIGGER USER");

        try
        {
            await db.ArtifactLocation.Where(location => location.TeamId == world.TeamId && objects.Contains(location.ArtifactObjectId))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(location => location.State, ArtifactLocationState.Deleted)
                    .SetProperty(location => location.Revision, location => location.Revision + 1));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE artifact_location ENABLE TRIGGER USER");
        }
    }

    /// <summary>An intent that has not finished and tries to name the object it is moving — the row the schema refuses.</summary>
    private static async Task<ArtifactTransferIntent> InFlightIntentAsync(CodeSpaceDbContext db, World world, Guid objectId)
    {
        var revisionId = await db.StorageProfileRevision.AsNoTracking()
            .Where(row => row.StorageProfileId == world.StorageProfileId && row.Revision == 1).Select(row => row.Id).SingleAsync();

        return new ArtifactTransferIntent
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revisionId,
            IdempotencyKey = $"retention-test/{objectId:N}", ExpectedDigest = System.Security.Cryptography.SHA256.HashData([7]),
            ExpectedSizeBytes = 1, TargetLocator = "local-rwx", TargetObjectKey = $"transfer/{objectId:N}",
            State = ArtifactTransferState.Intended, Revision = 1, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = world.ActorId,
            LastModifiedDate = DateTimeOffset.UtcNow, LastModifiedBy = world.ActorId,
        };
    }

    // ── What a sealed result cites ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A cleanup receipt and an offloaded event payload for the same run, so the seal has all four pin kinds to write.</summary>
    private async Task<(Guid ReceiptId, Guid ArtifactId)> SeedCitedRecordsAsync(World world)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var artifactId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        db.WorkflowArtifact.Add(new WorkflowArtifact
        {
            Id = artifactId, TeamId = world.TeamId, ContentType = "application/json", SizeBytes = 2,
            Sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([1, 2])).ToLowerInvariant(), InlineBytes = [1, 2],
        });
        db.AgentRunCleanupReceipt.Add(new AgentRunCleanupReceiptRecord
        {
            Id = receiptId, TeamId = world.TeamId, AgentRunId = world.AgentRunId, FenceEpoch = Fence,
            Kind = Messages.Agents.Recovery.RunResourceKind.Spool, Outcome = Messages.Agents.Recovery.RunResourceOutcome.Completed,
            RecordedByHost = "retention-test", RecordedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        db.AgentRunEvent.Add(new AgentRunEvent
        {
            Id = Guid.NewGuid(), AgentRunId = world.AgentRunId, Kind = Messages.Agents.AgentEventKind.Warning,
            Text = "an offloaded payload", DataArtifactId = artifactId,
        });
        await db.SaveChangesAsync();

        return (receiptId, artifactId);
    }

    /// <summary>
    /// A real seal through the real store: a hand-seeded protocol whose exact observation census is present, sealed as
    /// a replay so the runtime gate — which is a different slice's concern — stays out of this one.
    /// </summary>
    private async Task<Guid> SealResultAsync(World world)
    {
        var groupId = Guid.NewGuid();
        var manifest = new EvalSuiteManifest { Version = "sha256/retention-suite:v1", Cells = [new CorpusCellRef { TaskId = "task-a", Mode = BenchmarkMode.HarnessCli }] };
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.PairedQualificationProtocol.Add(new PairedQualificationProtocol
        {
            ObservationGroupId = groupId, TeamId = world.TeamId, SuiteDigest = "sha256:hidden", SuiteVersion = manifest.Version,
            CodeRevision = new string('c', 40), ControlModelRowId = world.ControlModelRowId, CandidateModelRowId = world.CandidateModelRowId,
            RequiresCellAdmission = false, RequiresResultDigest = false, StatisticsVersion = PairedQualificationOutcome.StatisticsVersion,
            Criterion = "Quality", SessionsPerCell = 1, MinimumIndependentClusters = 1, MinimumStrata = 1, MinimumRequiredExecutionClusters = 1,
            MinimumEvaluatorHealth = 1, MaxCostUsdPerLaunch = 3m, MinimumQualityLift = 0.05, OrderingSeed = "frozen-order",
            ProtocolDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(groupId.ToByteArray())),
        });
        foreach (var arm in new[] { "control", "candidate" })
            db.BenchmarkResultRecord.Add(Observation(world, groupId, manifest.Version, arm));
        await db.SaveChangesAsync();

        var outcome = Outcome(groupId, manifest.Version, db.PairedQualificationProtocol.Single(row => row.ObservationGroupId == groupId).ProtocolDigest);
        var store = new PairedQualificationResultStore(db, scope.Resolve<IQualificationRuntimeGate>());
        await store.SealAsync(new PairedQualificationSealRequest
        {
            ObservationGroupId = groupId, Manifest = manifest, Outcome = outcome, Source = PairedQualificationSealSource.Replay,
        }, CancellationToken.None);

        return groupId;
    }

    private BenchmarkResultRecord Observation(World world, Guid groupId, string suiteVersion, string arm) => new()
    {
        Id = Guid.NewGuid(), TeamId = world.TeamId, SuiteVersion = suiteVersion, TaskId = "task-a", Mode = BenchmarkMode.HarnessCli.ToString(),
        Harness = "claude-code", Model = arm, ModelCredentialModelId = arm == "control" ? world.ControlModelRowId : world.CandidateModelRowId,
        ObservationGroupId = groupId, ObservationArm = arm, ObservationSession = 0, AgentRunId = world.AgentRunId,
        ObservedModel = arm, OutcomeState = "Solved", Solved = true, RunStatus = AgentRunStatus.Succeeded.ToString(),
        MaxCostUsd = 3m, CostUsd = 1m, CostIndeterminate = false, GitSha = new string('c', 40),
    };

    private static PairedQualificationOutcome Outcome(Guid groupId, string suiteVersion, string protocolDigest) => new()
    {
        ObservationGroupId = groupId, ProtocolDigest = protocolDigest, CodeRevision = new string('c', 40), SuiteDigest = "sha256:hidden",
        SuiteVersion = suiteVersion, IndependentClusters = 1, PairedCells = 1, RequiredExecutionClusters = 1,
        Control = Arm(), Candidate = Arm(), QualityDifference = 0.1, QualityDifferenceLower95 = 0.06,
        RequiredExecutionComplete = true, QualifiedForCapabilityClaim = true, BlockingReasons = [], Strata = [], InfraFailures = [],
    };

    private static PairedArmQualificationSummary Arm() => new()
    {
        Solved = 1, BudgetAdmissibleSolved = 1, Total = 1, InfraUnknown = 0, CostKnownCells = 1,
        CapabilityVerdictCells = 1, ObservedModelCells = 1, EvaluatorHealth = 1, ObservedModels = ["model"],
    };

    private static string Identity(PairedQualificationResultPin pin) => $"{pin.Kind}:{pin.Target}";

    private async Task ForgetPinsAsync(Guid resultId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().PairedQualificationResultPin.Where(pin => pin.ResultId == resultId).ExecuteDeleteAsync();
    }

    /// <summary>
    /// Runs migration 0235's own backfill statements, read from the script that ships with the build rather than
    /// copied here — a mirror would keep passing after the migration changed underneath it.
    /// </summary>
    private async Task RunBackfillAsync()
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Persistence", "DbUpFiles", "0235_durable_retention_pins.sql"));
        var statements = script.Split(';').Select(value => value.Trim()).Where(value => value.Contains("INSERT INTO paired_qualification_result_pin", StringComparison.Ordinal)).ToList();

        statements.Count.ShouldBe(4, "0235 backfills four closures — runs, then their streams, receipts and offloaded payloads; if that count changed, so did what a pre-existing result protects");
        using var scope = _fixture.BeginScope();

        foreach (var statement in statements)
            await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(statement[statement.IndexOf("INSERT INTO", StringComparison.Ordinal)..]);
    }

    // ── Reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Describe(DurableRetentionSweepSummary summary) =>
        $"[claimed {summary.Claimed}, quarantined {summary.Quarantined}, collected {summary.Collected}, referenced {summary.Referenced}, kept {summary.Kept}]";

    private async Task<DurableRetentionSweepSummary> SweepAsync()
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<IDurableRetentionReaper>().SweepAsync(CancellationToken.None);
    }

    /// <summary>
    /// The real reaper over the real cursor, with only the CAS purge verdict swapped for the one a destination that
    /// will not give the bytes up produces. Everything that decides whether the tombstone is written is production
    /// code; the refusal is the single injected fact.
    /// </summary>
    private async Task<DurableRetentionSweepSummary> SweepAgainstARefusingDestinationAsync()
    {
        using var scope = _fixture.BeginScope();
        var options = scope.Resolve<DbContextOptions<CodeSpaceDbContext>>();
        var cursor = new LogStreamRetentionCursor(options, new RefusingPurgeCoordinator(), NullLogger<LogStreamRetentionCursor>.Instance);

        return await new DurableRetentionReaper(options, [cursor], NullLogger<DurableRetentionReaper>.Instance).SweepAsync(CancellationToken.None);
    }

    /// <summary>A destination that answers every delete with a refusal and no effect — the shape a revoked key or a read-only bucket produces.</summary>
    private sealed class RefusingPurgeCoordinator : IArtifactCasPurgeCoordinator
    {
        public Task<ArtifactCasPurgeResult> PurgeAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult<ArtifactCasPurgeResult>(new ArtifactCasPurgeResult.Rejected(new ArtifactCasProblem(ArtifactCasProblemCode.ProviderUnavailable, true)));

        public Task<ArtifactCasPurgeClaimResult> ClaimAsync(ArtifactCasPurgeRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArtifactCasPurgeResult> DeleteAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArtifactCasReleaseOutcome> ReleaseAsync(ArtifactCasPurgeClaim claim, ArtifactCasReleaseEvidence evidence, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ArtifactCasAbandonResult> AbandonAsync(ArtifactCasPurgeClaim claim, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private LogStreamRetentionCursor Cursor()
    {
        using var scope = _fixture.BeginScope();

        return new LogStreamRetentionCursor(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactCasPurgeCoordinator>(), NullLogger<LogStreamRetentionCursor>.Instance);
    }

    /// <summary>What the production claim query returns for the window the loop would build right now.</summary>
    private async Task<IReadOnlyList<DurableRetentionCandidate>> CandidatesAsync(int limit)
    {
        var rule = DurableRetentionPolicy.LogStream;
        var now = DateTimeOffset.UtcNow;

        return await Cursor().ClaimAsync(new DurableRetentionSweepWindow(now, now - rule.MinimumAge, now - rule.RecheckInterval), limit, CancellationToken.None);
    }

    private async Task<IReadOnlyList<Guid>> ClaimAsync(int limit) => (await CandidatesAsync(limit)).Select(candidate => candidate.Id).ToList();

    private async Task<AgentRunLogStream> StreamAsync(Guid streamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunLogStream.AsNoTracking().SingleAsync(row => row.Id == streamId);
    }

    private async Task<IReadOnlyList<PairedQualificationResultPin>> PinsAsync(Guid resultId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().PairedQualificationResultPin.AsNoTracking()
            .Where(pin => pin.ResultId == resultId).OrderBy(pin => pin.Kind).ToListAsync();
    }

    /// <summary>The ids of the transactions that inserted the result and its pins. One id is the whole atomicity claim.</summary>
    private async Task<IReadOnlyList<string>> InsertingTransactionsAsync(Guid resultId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().Database.SqlQueryRaw<string>(
            "SELECT DISTINCT xmin::text AS \"Value\" FROM paired_qualification_result WHERE observation_group_id = {0} "
            + "UNION SELECT DISTINCT xmin::text FROM paired_qualification_result_pin WHERE result_id = {0}", resultId).ToListAsync();
    }

    private async Task<IReadOnlyList<Guid>> ObjectsOfAsync(World world, Guid streamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunLogSegment.AsNoTracking()
            .Where(segment => segment.TeamId == world.TeamId && segment.StreamId == streamId)
            .Select(segment => segment.ArtifactObjectId).Distinct().OrderBy(id => id).ToListAsync();
    }

    private async Task<int> UnpurgedLocationsAsync(World world, Guid streamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        return await db.ArtifactLocation.AsNoTracking()
            .Where(location => location.TeamId == world.TeamId && location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted)
            .Join(db.AgentRunLogSegment.AsNoTracking().Where(segment => segment.StreamId == streamId),
                location => location.ArtifactObjectId, segment => segment.ArtifactObjectId, (location, _) => location.Id)
            .Distinct()
            .CountAsync();
    }

    private async Task<AgentRunLogMetadata> MetadataAsync(World world, Guid streamId)
    {
        using var scope = _fixture.BeginScope();

        return (await Logs(scope).GetMetadataAsync(world.TeamId, streamId, CancellationToken.None)).ShouldBeOfType<AgentRunLogMetadataResult.Found>().Metadata;
    }

    private async Task<AgentRunLogRangeResult> ReadAsync(World world, Guid streamId)
    {
        using var scope = _fixture.BeginScope();
        var stream = await StreamAsync(streamId);

        return await Logs(scope).ReadRangeAsync(new AgentRunLogRangeRequest(world.TeamId, streamId, 0, checked((int)stream.TotalBytes)), CancellationToken.None);
    }

    /// <summary>Whether the stream's bytes still come back through the production read path — the only reading of "the bytes are there" that matters.</summary>
    private async Task<bool> BytesReadableAsync(World world, Guid streamId) => await ReadAsync(world, streamId) is AgentRunLogRangeResult.Available;

    private static RoomProjector.AgentLogRow Row(AgentRunLogStream stream) =>
        new(stream.AgentRunId, stream.State, stream.SchemaVersion, stream.ManifestDigest != null, stream.RemoteStallSince != null, stream.PurgedAt != null);

    private static AgentRunLogService Logs(ILifetimeScope scope) =>
        new(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), scope.Resolve<IArtifactCasRuntimeCoordinator>(), TimeProvider.System);

    private static AgentRunLogOpenRequest Open(World world, Guid session, string kind = AgentRunLogKinds.StandardOutput) => new()
    {
        TeamId = world.TeamId, AgentRunId = world.AgentRunId, WorkerFenceEpoch = Fence, CaptureSessionId = session,
        StreamKind = kind, ContentType = AgentRunLogRepresentations.PlainTextContentType,
        ContentEncoding = AgentRunLogRepresentations.Utf8ContentEncoding, CaptureSource = "retention-test/v1",
    };

    /// <summary>EF's execution strategy wraps the server error; the guard's own message stays decisive.</summary>
    private static PostgresException PostgresErrorOf(Exception error)
    {
        while (error is not PostgresException && error.InnerException is { } inner) error = inner;
        return error.ShouldBeOfType<PostgresException>();
    }

    private const long Fence = 7;

    /// <summary>A team whose log storage route is Active, whose credential resolves, and whose local-rwx root is real — so a refused purge is only ever the one the test caused.</summary>
    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var root = NewRoot();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"durable-retention-{actorId:N}@test.local", Name = "Durable Retention" });
        db.Team.Add(new Team { Id = teamId, Slug = $"durable-retention-{teamId:N}", Name = "Durable Retention", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        var credential = new StorageCredential
        {
            Id = credentialId, TeamId = teamId, StableName = $"durable-retention-{credentialId:N}",
            CurrentRevision = 1, State = StorageCredentialState.Active, CreatedDate = now, CreatedBy = actorId,
        };
        credential.Revisions.Add(new StorageCredentialRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageCredentialId = credentialId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt("{}"),
            SafeHint = "safe", EnvelopeFingerprint = $"sha256:{new string('b', 64)}", CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageCredential.Add(credential);
        var profile = new StorageProfile
        {
            Id = profileId, TeamId = teamId, StableName = $"durable-retention-{profileId:N}", State = StorageProfileState.Active,
            CurrentRevision = 1, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        };
        profile.Revisions.Add(new StorageProfileRevision
        {
            Id = Guid.NewGuid(), TeamId = teamId, StorageProfileId = profileId, Revision = 1,
            ProviderTypeKey = LocalRwxArtifactStorageDriverFactory.TypeKey, NonSecretConfigJson = $"{{\"rootPath\":\"{root.Replace("\\", "\\\\")}\"}}",
            CredentialRef = $"db:{credentialId:D}:1", NamespaceFingerprint = $"sha256:{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(profileId.ToByteArray())).ToLowerInvariant()}",
            CreatedDate = now, CreatedBy = actorId,
        });
        db.StorageProfile.Add(profile);
        await db.SaveChangesAsync();
        var runId = Guid.NewGuid();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Running, TaskJson = "{}",
            FenceEpoch = Fence, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();

        return new World(teamId, actorId, profileId, runId, Guid.NewGuid(), Guid.NewGuid());
    }

    private string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"codespace-durable-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        _roots.Add(root);
        return root;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Reclaims every placement this class staged, through the same purge lifecycle the production plane uses, BEFORE
    /// the destinations it wrote them to are removed.
    ///
    /// <para>This is not tidiness. The artifact-location verifier is a bounded deployment-wide sweep — the hundred
    /// least-recently-verified placements across every team, on a database this collection shares — and its own tests
    /// assert that it reached THEIR rows. Live placements left behind a deleted local root change both what that batch
    /// contains and which destinations answer in it, so a class that stages placements owns them all the way to the
    /// end.</para>
    /// </summary>
    public async Task DisposeAsync()
    {
        foreach (var staged in _staged) await ReclaimAsync(staged);

        foreach (var root in _roots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task ReclaimAsync(StagedStream staged)
    {
        try
        {
            using var scope = _fixture.BeginScope();
            var db = scope.Resolve<CodeSpaceDbContext>();
            var purge = scope.Resolve<IArtifactCasPurgeCoordinator>();
            var objects = db.AgentRunLogSegment.AsNoTracking()
                .Where(segment => segment.TeamId == staged.TeamId && segment.StreamId == staged.StreamId)
                .Select(segment => segment.ArtifactObjectId).Distinct();

            // Per LOCATION, not per object: identical captures deduplicate to one object with a placement per write,
            // and a claim that does not name which placement refuses a replicated object outright — which is the
            // production behaviour, and the reason this cleanup has to be explicit about it.
            var placements = await db.ArtifactLocation.AsNoTracking()
                .Where(location => location.TeamId == staged.TeamId && objects.Contains(location.ArtifactObjectId)
                    && location.State != ArtifactLocationState.Purged && location.State != ArtifactLocationState.Deleted)
                .Select(location => new { location.Id, location.ArtifactObjectId })
                .ToListAsync();

            foreach (var placement in placements)
                await purge.PurgeAsync(new ArtifactCasPurgeRequest
                {
                    TeamId = staged.TeamId, ArtifactObjectId = placement.ArtifactObjectId,
                    ArtifactLocationId = placement.Id, ActorId = Messages.Constants.SystemUsers.SeederId,
                }, CancellationToken.None);
        }
        catch { /* best-effort: a test that already purged, or left a refusing destination, has nothing more to give back */ }
    }

    private sealed record StagedStream(Guid TeamId, Guid StreamId);

    private sealed record World(Guid TeamId, Guid ActorId, Guid StorageProfileId, Guid AgentRunId, Guid ControlModelRowId, Guid CandidateModelRowId);
}
