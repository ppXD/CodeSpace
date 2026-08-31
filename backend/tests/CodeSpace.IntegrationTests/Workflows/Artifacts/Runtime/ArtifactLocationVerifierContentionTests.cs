using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// What one row the database refuses to record costs the rest of the sweep.
///
/// <para>A pass reads a batch, then settles each row against the <c>xmin</c> it was read with. Two passes over the same
/// population — two workers, or an hourly job overlapping a slow predecessor — put two writers on one row, and the
/// loser's save is refused. A refusal that escapes the row ends the loop, the job reports failed to Hangfire, and every
/// row behind the contended one goes unasked-about for another hour. The rows behind it are the whole reason the sweep
/// exists, so one row nobody could write must cost exactly one row — and the rows that WERE written must still be there
/// afterwards.</para>
///
/// <para>Every fault is injected from INSIDE the pass, because the schema leaves no way to stage one from outside:
/// <c>artifact_location_event_guard</c> admits an entry only at the location's current or immediately next revision,
/// and the deferred <c>artifact_location_event_require_location</c> rejects any entry the committed row has not reached.
/// A ledger that could be pre-poisoned is a ledger with a hole in it, so there is nothing to pre-poison. The competing
/// writer therefore runs on the pass's first driver activation — after the batch is read, before any row is settled.
/// The refused restore executes a statement the real server rejects on its second location write, and the refused
/// commit advances the row once more on the settle's own transaction, which the deferred constraint rejects at COMMIT.
/// Each failure arrives from the real database at the exact production boundary the test names.</para>
///
/// <para>Assertions are about rows this class seeded, never about the summary's counts — <c>StaleAsync</c> takes the
/// oldest rows across every team, so any leftover in this suite satisfies a bound on a tally. The exceptions are the
/// two single-row batches below, and only because each one first names the row it got: a batch of one is a batch of
/// exactly one row, but WHICH row is a separate claim, and nothing in a tally makes it. They make it by recording the
/// destination the pass actually opened, which on a freshly seeded team belongs to that team's one placement.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactLocationVerifierContentionTests : IAsyncLifetime
{
    /// <summary>Old enough that these rows are the front of the deployment-wide batch, so a pass that reached them is not a pass that got lucky.</summary>
    private static readonly TimeSpan Ancient = TimeSpan.FromDays(4000);

    /// <summary>Comfortably wider than the batch these tests seed, so the recovery share other tests' Missing rows take cannot squeeze them out.</summary>
    private const int BatchSize = 200;

    /// <summary>The size every placement here records, and the size the object written for it has to be, or the verifier reads the destination as holding something else.</summary>
    private const int ObjectSize = 12;

    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _placed = [];
    private readonly List<string> _roots = [];

    public ArtifactLocationVerifierContentionTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_row_whose_settle_the_database_refuses_is_left_exactly_as_it_was()
    {
        var team = await SeedTeamAsync();
        var refused = await PlaceAsync(team, Ancient + TimeSpan.FromDays(300), ArtifactLocationState.Available);
        var behind = await PlaceAsync(team, Ancient + TimeSpan.FromDays(299), ArtifactLocationState.Available);
        var before = await LocationAsync(refused.Id);

        before.State.ShouldBe(ArtifactLocationState.Available, "both rows must start unanswered, or their later states prove nothing");

        using var pass = RacedScope(refused.Id);
        await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(BatchSize, CancellationToken.None);

        var after = await LocationAsync(refused.Id);
        after.Revision.ShouldBe(before.Revision + 1, "the winner's revision must be the last one: the loser may leave none of its own");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "and the row must not look freshly checked — verified_at is also the sweep's cursor, and the loser observed nothing it could record");
        after.State.ShouldBe(ArtifactLocationState.Available, "a row this pass could not record must not be demoted on the strength of a write that failed");
        (await EventCountAsync(refused.Id)).ShouldBe(2, "the ledger must hold the row's own entry and the winner's, and nothing of the loser");

        (await LocationAsync(behind.Id)).State.ShouldBe(ArtifactLocationState.Missing, "and the pass must carry on to the row behind the one it lost");
    }

    [Fact]
    public async Task One_refused_row_does_not_cost_the_other_ninety_nine_their_check()
    {
        // The headline: a refused row at the FRONT of the batch. Before containment this ended the whole pass, so a
        // single row two workers happened to race on stopped ninety-nine healthy placements from ever being examined —
        // silently, because the operator sees a failed Hangfire job and no row-level detail at all.
        var team = await SeedTeamAsync();
        var refused = await PlaceAsync(team, Ancient + TimeSpan.FromDays(200), ArtifactLocationState.Available);
        var behind = await PlaceManyAsync(team, count: 99, oldest: Ancient + TimeSpan.FromDays(199));

        (await AnsweredAsync(behind)).ShouldBe(0, "none of the ninety-nine may already be answered, or this proves nothing about the pass");

        using var pass = RacedScope(refused.Id);
        await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(BatchSize, CancellationToken.None);

        (await AnsweredAsync(behind)).ShouldBe(99, "one row the pass could not write must cost exactly one row");
        (await LocationAsync(refused.Id)).State.ShouldBe(ArtifactLocationState.Available, "and the one it could not write must still be the row it found");
    }

    [Fact]
    public async Task A_pass_dispatched_as_a_command_leaves_every_row_it_settled_durable()
    {
        // The path production actually takes. The job dispatches this through the mediator, and TransactionalBehavior
        // opens ONE explicit transaction around the handler — so a verifier writing on the ambient context poisons that
        // transaction with the first refused row, turns every row after it into "current transaction is aborted", and
        // the block is rolled back with every row the pass HAD settled inside it. Calling the service directly cannot
        // see any of that, which is why what follows goes through Send and re-reads every row in a fresh scope
        // afterwards.
        var team = await SeedTeamAsync();
        var refused = await PlaceAsync(team, Ancient + TimeSpan.FromDays(400), ArtifactLocationState.Available);
        var behind = await PlaceManyAsync(team, count: 5, oldest: Ancient + TimeSpan.FromDays(399));

        (await AnsweredAsync(behind)).ShouldBe(0, "none of them may already be answered, or this proves nothing about the command");

        using (var pass = RacedScope(refused.Id))
        {
            // Returning at all is half the claim: before containment the refused row threw, and the job failed.
            await pass.Resolve<IMediator>().Send(new VerifyStaleArtifactLocationsCommand());
        }

        (await AnsweredAsync(behind)).ShouldBe(5, "every row the command settled has to still be settled once its transaction is over — a row the pass could not write must not discard the rows it could");
        (await LocationAsync(refused.Id)).State.ShouldBe(ArtifactLocationState.Available, "and the row it could not write must still be the row it found");
    }

    [Fact]
    public async Task A_restore_whose_second_location_write_is_refused_leaves_the_row_exactly_as_it_found_it()
    {
        // A restore is TWO advances of the row — the confirmation, then the return to Available — and they have to be
        // one unit. Settled separately, a second write the database refuses leaves the first standing: the row the pass
        // reports as unrecorded has had its verified_at moved anyway, so the sweep's cursor advanced past an
        // observation nothing in the ledger explains and the row is not looked at again for a full cycle.
        var team = await SeedTeamAsync();
        var refused = await PlaceAsync(team, Ancient + TimeSpan.FromDays(500), ArtifactLocationState.Missing);

        Materialize(refused);

        var before = await LocationAsync(refused.Id);
        var refusal = new RefusedSecondLocationWrite();

        using var pass = FaultedScope(refusal);
        var summary = await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(batchSize: 1, CancellationToken.None);

        refusal.LocationWrites.ShouldBe(2, "the restore has to have completed its first save and attempted its second, or nothing below is about atomicity at all");
        refusal.Refusals.ShouldBe(1, "the real database failure must land on exactly the second location write");

        var after = await LocationAsync(refused.Id);
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "the first half of a refused restore must not survive the second half being refused: an outcome that recorded nothing has to leave verified_at where it was");
        after.Revision.ShouldBe(before.Revision, "and must carry no revision the ledger does not also carry");
        after.State.ShouldBe(ArtifactLocationState.Missing, "and must still be the row the pass found");
        (await EventCountAsync(refused.Id)).ShouldBe(1, "neither half may survive in the ledger — only the row's own entry");

        // The tally is honest here and in the commit test below, and nowhere else in this class — but only because of
        // the line that follows. A batch of one is a batch of exactly one row; WHICH row is a separate claim, and the
        // clock reads above cannot make it, since they count reads without saying whose row they were for. Every
        // assertion above is equally true of a batch that selected a neighbour's leftover and never touched this row
        // at all, so the tally is pinned to this row by the destination the pass actually went to.
        pass.Resolve<RecordingBroker>().Teams.ShouldBe([team.TeamId], "the one row the batch selected has to have been this one: the tally is about this row only if the pass opened this team's destination and no other");

        summary.Checked.ShouldBe(1);
        summary.Unrecorded.ShouldBe(1, "a row the provider answered about and the database would not record is its own outcome");
        summary.Inconclusive.ShouldBe(0, "and must not be filed as a destination that could not answer — it answered perfectly well");
    }

    [Fact]
    public async Task A_verifier_behind_the_writer_records_its_honest_observation_instead_of_silently_losing_the_row()
    {
        var isolated = new PostgresFixture();
        Destination? team = null;

        try
        {
            await isolated.InitializeAsync();
            team = await SeedTeamAsync(isolated);
            var placed = await PlaceAsync(isolated, team, TimeSpan.FromHours(13), ArtifactLocationState.Available);
            Materialize(placed);

            using (var census = isolated.BeginScope())
            {
                var locationIds = await census.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
                    .Select(location => location.Id).ToListAsync();
                locationIds.ShouldBe([placed.Id],
                    "this verifier runs against a database containing exactly its target; no deployment-wide neighbour can crowd it out");
            }

            var before = await LocationAsync(isolated, placed.Id);
            var observedAt = before.CreatedDate - TimeSpan.FromMinutes(7);
            var clock = new FixedClock(observedAt);

            using var pass = ClockScope(isolated, clock);
            await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(batchSize: 1, CancellationToken.None);

            pass.Resolve<RecordingBroker>().Teams.ShouldBe([team.TeamId],
                "the production selector must have opened this exact controlled placement, independently of any sweep tally");
            clock.Reads.ShouldBe(1, "one successful HEAD of an Available placement records one observation");

            var after = await LocationAsync(isolated, placed.Id);
            after.State.ShouldBe(ArtifactLocationState.Available);
            after.Revision.ShouldBe(before.Revision + 1);
            after.VerifiedAt.ShouldBe(observedAt,
                "verified_at is the verifier's honest observation time, not a value moved forward to appease another machine's creation clock");
            (await EventCountAsync(isolated, placed.Id)).ShouldBe(2,
                "the accepted cross-clock observation must still have its exact append-only snapshot");
        }
        finally
        {
            await isolated.DisposeAsync();

            if (team is not null)
            {
                try
                {
                    if (Directory.Exists(team.Root)) Directory.Delete(team.Root, recursive: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    [Fact]
    public async Task A_settle_whose_commit_is_refused_is_unrecorded_and_not_inconclusive()
    {
        // The transaction that makes a restore atomic brought two failure points a settle did not have before — the
        // BEGIN and the COMMIT — and neither of them raises DbUpdateException: EF hands the provider's own exception
        // straight through, and DbUpdateException does not derive from it. This schema in particular checks DEFERRED
        // constraints at COMMIT, so a refused commit is an ordinary outcome here rather than an exotic one. Filed as
        // Inconclusive it says the DESTINATION could not answer, and sends an operator to look at a bucket that is
        // perfectly healthy; the truth is the opposite — the destination answered, and this pass could not write the
        // answer down.
        var team = await SeedTeamAsync();
        var refused = await PlaceAsync(team, Ancient + TimeSpan.FromDays(600), ArtifactLocationState.Missing);
        var before = await LocationAsync(refused.Id);
        var commit = new RefusedCommit(refused.Id);

        using var pass = FaultedScope(commit);
        var summary = await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(batchSize: 1, CancellationToken.None);

        pass.Resolve<RecordingBroker>().Teams.ShouldBe([team.TeamId], "the one row the batch selected has to have been this one, or the tally below is about somebody else's row");
        commit.Poisonings.ShouldBe(1, "the settle has to have reached its COMMIT and had it refused, or this is a test of some earlier failure");

        var after = await LocationAsync(refused.Id);
        after.Revision.ShouldBe(before.Revision, "a commit the database refused must leave the row at the revision the pass found it on");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "and must not look freshly checked — verified_at is also the sweep's cursor, and nothing was recorded");
        after.State.ShouldBe(ArtifactLocationState.Missing, "and must still be the row the pass found");
        (await EventCountAsync(refused.Id)).ShouldBe(1, "and must leave nothing in the ledger but the row's own entry");

        summary.Checked.ShouldBe(1);
        summary.Unrecorded.ShouldBe(1, "a commit this deployment's own database refused is US failing to write down what we saw");
        summary.Inconclusive.ShouldBe(0, "and must never be reported as the destination failing to answer — it answered, and the answer is the thing that was lost");
    }

    [Fact]
    public async Task A_row_whose_first_read_is_refused_costs_exactly_that_row()
    {
        // The same failure as the headline, one line above where it was contained. The statement that OPENS a row's
        // work — reading the profile revision the location was written under — is a database call like any other, and
        // it sat outside the try that holds the row. A pool exhausted for a second, or a database that blinked, ended
        // the whole pass right there and took every row behind it, which is the exact loss the containment exists to
        // prevent. Nothing about the row is special: it is the first thing tried, so it is the first thing to fail.
        var unreadable = await SeedTeamAsync();
        var healthy = await SeedTeamAsync();
        var refused = await PlaceAsync(unreadable, Ancient + TimeSpan.FromDays(700), ArtifactLocationState.Available);
        var behind = await PlaceManyAsync(healthy, count: 5, oldest: Ancient + TimeSpan.FromDays(699));
        var before = await LocationAsync(refused.Id);
        var refusal = new RefusedRevisionRead(unreadable.TeamId);

        (await AnsweredAsync(behind)).ShouldBe(0, "none of the rows behind it may already be answered, or nothing below is about the pass carrying on");

        using var pass = FaultedScope(refusal);
        var summary = await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(BatchSize, CancellationToken.None);

        refusal.Refusals.ShouldBe(1, "the pass has to have reached this row and had its opening read refused — no refusal means the fault never landed and everything below is vacuous");

        var after = await LocationAsync(refused.Id);
        after.Revision.ShouldBe(before.Revision, "a row whose profile revision this pass could not even read must be left exactly as it was");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "and must not look freshly checked — verified_at is also the sweep's cursor, and nothing was observed");
        after.State.ShouldBe(ArtifactLocationState.Available, "and must still be the row the pass found");
        (await EventCountAsync(refused.Id)).ShouldBe(1, "and must leave nothing in the ledger but the row's own entry");

        (await AnsweredAsync(behind)).ShouldBe(5, "one row the pass could not read must cost exactly one row");

        // The verdict, in the one form a deployment-wide batch can carry honestly. A tally of Inconclusive is a lower
        // bound that any leftover row in this database satisfies; a tally of Unrecorded is an upper bound nothing can
        // satisfy vacuously — and Unrecorded is precisely the wrong answer here. It would tell an operator this pass
        // could not write down what it saw of a row where nothing had been seen yet.
        summary.Unrecorded.ShouldBe(0, "nothing about the object had been observed when this read failed, so nothing failed to be written down: the honest verdict is Inconclusive");
    }

    [Fact]
    public async Task A_row_that_will_record_nothing_is_inconclusive_however_the_database_is_behaving()
    {
        // The mirror of the commit test above, pointing the other way. The settle opened its context and its
        // transaction BEFORE the row's verdict was known, so a database failure on a row whose true verdict is
        // Inconclusive came back Unrecorded — "we could not write down what we saw", about a row where nothing was
        // seen. That sends an operator to look at a database that is fine, and away from the destination that went
        // away, which is the one thing this row is actually telling them.
        var team = await SeedTeamAsync();
        var absent = await PlaceAsync(team, Ancient + TimeSpan.FromDays(800), ArtifactLocationState.Missing);
        var before = await LocationAsync(absent.Id);
        var refusal = new RefusedTransaction();

        // Why this row records nothing: every object under a vanished root reads as absent, and a destination that
        // cannot answer at all cannot testify that one of its objects was deleted. So the pass observes nothing it is
        // entitled to write down — and must therefore write nothing down, including by trying and failing.
        Vanish(team);

        using var pass = FaultedScope(refusal);
        var summary = await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(batchSize: 1, CancellationToken.None);

        pass.Resolve<RecordingBroker>().Teams.ShouldBe([team.TeamId], "the one row the batch selected has to have been this one, or the tally below is about somebody else's row");
        refusal.Refusals.ShouldBe(0, "a row that was never going to write anything must not open a transaction at all — opening one is the whole mechanism by which a database blip becomes a verdict about an observation nobody made");

        summary.Checked.ShouldBe(1);
        summary.Inconclusive.ShouldBe(1, "a destination that could not answer is Inconclusive, and stays Inconclusive however the database is behaving");
        summary.Unrecorded.ShouldBe(0, "and must never be Unrecorded: Unrecorded claims an observation, and there was none");

        var after = await LocationAsync(absent.Id);
        after.Revision.ShouldBe(before.Revision, "and the row must be left exactly as it was");
        after.VerifiedAt.ShouldBe(before.VerifiedAt, "including its stale verified_at, which is the honest record of when it was last actually known");
        after.State.ShouldBe(ArtifactLocationState.Missing, "and must still be the row the pass found");
        (await EventCountAsync(absent.Id)).ShouldBe(1, "and the ledger must hold nothing but the row's own entry");
    }

    // ─── World ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A scope whose verifier meets a second writer on the row named, in the only window where one can exist.
    ///
    /// <para>The competing write lands on the pass's FIRST driver activation: the batch is already read, so the pass is
    /// holding a generation-old <c>xmin</c> for every row in it, and nothing is settled yet, so no row is locked and
    /// the write cannot deadlock against the pass itself. Which row triggers it does not matter — what matters is that
    /// it lands before the named row is settled, and the first activation always does.</para>
    /// </summary>
    private ILifetimeScope RacedScope(Guid locationId) => _fixture.BeginScope(builder => builder
        .Register<IStorageRuntimeDriverBroker>(context => new RacingBroker(context.Resolve<StorageRuntimeDriverBroker>(), () => AdvanceAsync(locationId, _ => { })))
        .InstancePerLifetimeScope());

    /// <summary>
    /// A scope whose verifier — and only the verifier — reads a fixed wall clock.
    /// The broker recorder independently names the exact destination the production selector visited.
    /// </summary>
    private static ILifetimeScope ClockScope(PostgresFixture fixture, TimeProvider clock) => fixture.BeginScope(builder =>
    {
        RecordDestinations(builder);
        builder.Register<IArtifactLocationVerifier>(context => new ArtifactLocationVerifier(
            context.Resolve<DbContextOptions<CodeSpaceDbContext>>(), context.Resolve<IStorageRuntimeDriverBroker>(), clock, context.Resolve<ILogger<ArtifactLocationVerifier>>()))
            .InstancePerLifetimeScope();
    });

    /// <summary>
    /// A scope whose verifier — and only the verifier — meets the named fault on the connections it opens for itself.
    ///
    /// <para>The interceptor goes on the options the verifier builds its per-row contexts from, so nothing else in the
    /// scope is touched: the broker's own profile and credential reads run on the container's context and answer
    /// normally. That is what keeps each of these tests about the single statement it faults, rather than about a
    /// deployment whose database has gone away entirely.</para>
    ///
    /// <para>Every fault below is staged as an interceptor that makes the REAL server refuse a real statement, rather
    /// than as a thrown stand-in: what the verifier meets is a genuine <c>PostgresException</c> arriving out of the
    /// genuine call, which is the whole distinction its two guards are sorting on.</para>
    /// </summary>
    private ILifetimeScope FaultedScope(IInterceptor fault) => _fixture.BeginScope(builder =>
    {
        RecordDestinations(builder);
        builder.Register<IArtifactLocationVerifier>(context => new ArtifactLocationVerifier(
            new DbContextOptionsBuilder<CodeSpaceDbContext>(context.Resolve<DbContextOptions<CodeSpaceDbContext>>()).AddInterceptors(fault).Options,
            context.Resolve<IStorageRuntimeDriverBroker>(), context.Resolve<TimeProvider>(), context.Resolve<ILogger<ArtifactLocationVerifier>>()))
            .InstancePerLifetimeScope();
    });

    /// <summary>Puts a recorder in front of the real broker, so a test can name the row a batch of one actually selected.</summary>
    private static void RecordDestinations(ContainerBuilder builder) => builder
        .Register(context => new RecordingBroker(context.Resolve<StorageRuntimeDriverBroker>()))
        .AsSelf().As<IStorageRuntimeDriverBroker>()
        .InstancePerLifetimeScope();

    /// <summary>Takes the whole destination away — an unmounted volume, a detached disk — so its objects read as absent and it can no longer testify that any of them was deleted.</summary>
    private static void Vanish(Destination destination) => Directory.Delete(destination.Root, recursive: true);

    /// <summary>Puts the object the placement names at the destination, at the size the row records, so a HEAD agrees with it.</summary>
    private static void Materialize(Placed placed)
    {
        var path = Path.Combine([placed.Root, "objects", .. placed.ObjectKey.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[ObjectSize]);
    }

    /// <summary>
    /// Advances a row one revision with the byte-identical ledger entry the schema demands, through a context of its
    /// own. <c>verified_at</c> is deliberately left alone so a test can assert the loser moved it by comparing against
    /// the value it was seeded with.
    /// </summary>
    private async Task AdvanceAsync(Guid locationId, Action<ArtifactLocation> change)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var location = await db.ArtifactLocation.SingleAsync(row => row.Id == locationId);

        change(location);
        location.Revision++;
        location.LastModifiedDate = DateTimeOffset.UtcNow;
        db.ArtifactLocationEvent.Add(Snapshot(location));

        await db.SaveChangesAsync();
    }

    private static ArtifactLocationEvent Snapshot(ArtifactLocation location) => new()
    {
        Id = Guid.NewGuid(), TeamId = location.TeamId, ArtifactLocationId = location.Id, Revision = location.Revision,
        EventType = ArtifactLocationEventType.Verified, State = location.State, ObservedAt = DateTimeOffset.UtcNow,
        ProviderObjectVersion = location.ProviderObjectVersion, ProviderETag = location.ProviderETag,
        ProviderChecksumAlgorithm = location.ProviderChecksumAlgorithm, ProviderChecksum = location.ProviderChecksum,
        ObservedSizeBytes = location.ObservedSizeBytes, VerifiedAt = location.VerifiedAt,
        ContentEncoding = location.ContentEncoding, EncryptionKeyVersion = location.EncryptionKeyVersion,
        ErrorCode = location.LastErrorCode, ErrorMessage = location.LastErrorMessage, DetailsJson = "{}",
    };

    private Task<ArtifactLocation> LocationAsync(Guid locationId) => LocationAsync(_fixture, locationId);

    private static async Task<ArtifactLocation> LocationAsync(PostgresFixture fixture, Guid locationId)
    {
        using var scope = fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking().SingleAsync(location => location.Id == locationId);
    }

    /// <summary>How many entries this row's append-only ledger holds, which is the only place half of a refused write could still be hiding.</summary>
    private Task<int> EventCountAsync(Guid locationId) => EventCountAsync(_fixture, locationId);

    private static async Task<int> EventCountAsync(PostgresFixture fixture, Guid locationId)
    {
        using var scope = fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocationEvent.AsNoTracking().CountAsync(entry => entry.ArtifactLocationId == locationId);
    }

    /// <summary>How many of these rows the sweep reached a conclusion about. Their objects were never written, so a row that was asked about comes back Missing.</summary>
    private async Task<int> AnsweredAsync(IReadOnlyCollection<Placed> placed)
    {
        var locationIds = placed.Select(row => row.Id).ToList();

        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .CountAsync(location => locationIds.Contains(location.Id) && location.State == ArtifactLocationState.Missing);
    }

    private async Task<Destination> SeedTeamAsync()
    {
        var destination = await SeedTeamAsync(_fixture);

        _roots.Add(destination.Root);

        return destination;
    }

    private static async Task<Destination> SeedTeamAsync(PostgresFixture fixture)
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(fixture);
        var routed = await RoutedArtifactSeed.RouteTeamAsync(fixture, teamId, actorId);

        return new Destination(teamId, routed.ProfileId, routed.Root);
    }

    private async Task<IReadOnlyList<Placed>> PlaceManyAsync(Destination destination, int count, TimeSpan oldest)
    {
        var placed = new List<Placed>();

        foreach (var index in Enumerable.Range(0, count)) placed.Add(await PlaceAsync(destination, oldest - TimeSpan.FromMinutes(index), ArtifactLocationState.Available));

        return placed;
    }

    /// <summary>
    /// One placement on this team's own routed destination, at the revision every ledger starts on.
    ///
    /// <para>The profile revision is looked up by the profile this destination actually is, not merely the team's
    /// newest: a team carrying a second profile would otherwise get a row whose bytes are expected somewhere this test
    /// never writes, and a restore that could never happen would read as a containment failure.</para>
    /// </summary>
    private async Task<Placed> PlaceAsync(Destination destination, TimeSpan age, ArtifactLocationState state)
    {
        var placed = await PlaceAsync(_fixture, destination, age, state);
        _placed.Add(placed.Id);

        return placed;
    }

    private static async Task<Placed> PlaceAsync(PostgresFixture fixture, Destination destination, TimeSpan age, ArtifactLocationState state)
    {
        using var scope = fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var revisionId = await db.StorageProfileRevision.AsNoTracking().Where(revision => revision.StorageProfileId == destination.ProfileId)
            .OrderByDescending(revision => revision.Revision).Select(revision => revision.Id).FirstAsync();
        var observed = DateTimeOffset.UtcNow - age;
        var objectId = Guid.NewGuid();
        var checksum = System.Security.Cryptography.SHA256.HashData(objectId.ToByteArray());

        db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = destination.TeamId, Digest = checksum, SizeBytes = ObjectSize, CreatedDate = observed });

        var location = new ArtifactLocation
        {
            Id = Guid.NewGuid(), TeamId = destination.TeamId, ArtifactObjectId = objectId, StorageProfileRevisionId = revisionId,
            Locator = "local://contention", ObjectKey = $"objects/{objectId:N}", State = state, VerifiedAt = observed,
            Revision = 1, CreatedDate = observed, LastModifiedDate = observed,
            ObservedSizeBytes = ObjectSize, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = checksum,
        };
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(Snapshot(location));

        await db.SaveChangesAsync();
        return new Placed(location.Id, location.ObjectKey, destination.Root);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Takes every row this class placed permanently out of the sweep.
    ///
    /// <para>They are seeded thousands of days stale so they are the FRONT of the deployment-wide batch, which is the
    /// only way to be sure the pass under test actually reached them — and that makes them the front of every LATER
    /// test's batch too. A hundred of them is far more than the recovery share holds, so the one row a neighbouring
    /// test owns would never get a slot. <c>Deleted</c> is terminal and no pass ever selects it. Best-effort, and on
    /// the failure path too: a failing test that leaks these breaks its neighbours rather than itself.</para>
    /// </summary>
    public async Task DisposeAsync()
    {
        foreach (var locationId in _placed)
        {
            try
            {
                await AdvanceAsync(locationId, location => location.State = ArtifactLocationState.Deleted);
            }
            catch (DbUpdateException) { }
        }

        foreach (var root in _roots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The second writer, fired once, on the first driver the pass activates.</summary>
    private sealed class RacingBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;
        private readonly Func<Task> _race;
        private int _raced;

        public RacingBroker(IStorageRuntimeDriverBroker inner, Func<Task> race)
        {
            _inner = inner;
            _race = race;
        }

        public async ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _raced, 1) == 0) await _race();

            return await _inner.OpenAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Passes every activation through untouched and remembers whose destination it was for.
    ///
    /// <para>A batch of one is a batch of exactly one row, but nothing in a tally says WHICH — the sweep takes the
    /// oldest rows in this database across every team, so a row a neighbouring test left behind satisfies "one row was
    /// checked" exactly as well as a seeded one does. Each test here seeds its own team and one placement on it, so
    /// the team the pass opened a destination for names the row it selected.</para>
    /// </summary>
    private sealed class RecordingBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;

        public RecordingBroker(IStorageRuntimeDriverBroker inner) => _inner = inner;

        public List<Guid> Teams { get; } = [];

        public ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            Teams.Add(request.TeamId);

            return _inner.OpenAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// Makes the settle's COMMIT fail, using the schema's own deferred constraint rather than a stand-in for it.
    ///
    /// <para>Immediately before the settle commits, this advances the named row one further revision on the settle's
    /// own transaction. The immediate guard permits it — a location revision may advance by exactly one — and the
    /// DEFERRED <c>artifact_location_require_event</c> then rejects it at COMMIT, because no ledger entry explains
    /// that revision. So the failure the verifier meets is a real <c>PostgresException</c> raised by the real
    /// constraint at the real commit, arriving out of <c>CommitAsync</c> and not out of <c>SaveChangesAsync</c> —
    /// which is the whole distinction under test, since only the second is a <c>DbUpdateException</c>.</para>
    ///
    /// <para>It is deliberately NOT conditional on which row the transaction is settling: a pass that selected some
    /// other row would poison this one's commit just the same, which is why the test names the row the pass actually
    /// went to before it reads the tally.</para>
    /// </summary>
    private sealed class RefusedCommit : DbTransactionInterceptor
    {
        private readonly Guid _locationId;

        public RefusedCommit(Guid locationId) => _locationId = locationId;

        /// <summary>How many commits this refused, so a test can tell "the commit failed" from "the commit was never reached".</summary>
        public int Poisonings { get; private set; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            await using var command = transaction.Connection!.CreateCommand();
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = _locationId;

            command.Transaction = transaction;
            command.CommandText = "UPDATE artifact_location SET revision = revision + 1 WHERE id = @id";
            command.Parameters.Add(parameter);

            await command.ExecuteNonQueryAsync(cancellationToken);
            Poisonings++;

            return result;
        }
    }

    /// <summary>
    /// Refuses the second real location UPDATE of a restore from inside its real transaction.
    ///
    /// <para>The first UPDATE and event have already been accepted when this fires. Executing a division by zero on
    /// the same connection and transaction produces a real Postgres <see cref="DbException"/> and aborts that block,
    /// so the test proves transaction rollback rather than relying on an unrelated schema check as its fault source.</para>
    /// </summary>
    private sealed class RefusedSecondLocationWrite : DbCommandInterceptor
    {
        public int LocationWrites { get; private set; }
        public int Refusals { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("UPDATE artifact_location", StringComparison.OrdinalIgnoreCase)) return result;

            LocationWrites++;
            if (LocationWrites != 2) return result;

            Refusals++;
            await using var poison = command.Connection!.CreateCommand();
            poison.Transaction = command.Transaction;
            poison.CommandText = "SELECT 1 / 0";
            await poison.ExecuteScalarAsync(cancellationToken);

            return result;
        }
    }

    /// <summary>
    /// Makes the read that OPENS a row's work fail, for one named team's rows and nothing else.
    ///
    /// <para>Selective on purpose: the point of the test is that the rows BEHIND the refused one are still checked, so
    /// every other row's reads have to succeed. The profile-revision read is the only statement in a pass that carries
    /// a team id and names that table, and the poisoned team owns exactly one placement, so faulting on both together
    /// names the row without needing a tally to.</para>
    ///
    /// <para>The refusal is a real division by zero executed against the real server on the read's own connection, so
    /// what leaves here is a genuine <c>PostgresException</c> arriving out of the genuine read — the shape of a
    /// momentarily exhausted pool or a database that blinked, which is what this read fails for in production.</para>
    /// </summary>
    private sealed class RefusedRevisionRead : DbCommandInterceptor
    {
        private readonly Guid _teamId;

        public RefusedRevisionRead(Guid teamId) => _teamId = teamId;

        /// <summary>How many opening reads this refused, so a test can tell "the read failed" from "the row was never reached".</summary>
        public int Refusals { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!IsRevisionReadForTeam(command)) return result;

            Refusals++;

            await using var poison = command.Connection!.CreateCommand();
            poison.Transaction = command.Transaction;
            poison.CommandText = "SELECT 1 / 0";

            await poison.ExecuteScalarAsync(cancellationToken);

            return result;
        }

        private bool IsRevisionReadForTeam(DbCommand command) =>
            command.CommandText.Contains("storage_profile_revision", StringComparison.Ordinal)
            && command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, _teamId));
    }

    /// <summary>
    /// Makes the settle's BEGIN fail, and counts how often it was even asked to.
    ///
    /// <para>The count is half the test: a settle that decides its verdict before it opens anything never starts a
    /// transaction for a row that records nothing, so zero refusals IS the property, and one refusal is the code
    /// opening a write it was never going to make. As above, the failure is a real statement the real server refuses,
    /// so a pass that does reach here meets the <c>DbException</c> that would file the row as <c>Unrecorded</c>.</para>
    /// </summary>
    private sealed class RefusedTransaction : DbTransactionInterceptor
    {
        /// <summary>How many transaction starts this refused — and, at zero, that none was attempted.</summary>
        public int Refusals { get; private set; }

        public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result, CancellationToken cancellationToken = default)
        {
            Refusals++;

            await using var poison = connection.CreateCommand();
            poison.CommandText = "SELECT 1 / 0";

            await poison.ExecuteScalarAsync(cancellationToken);

            return result;
        }
    }

    /// <summary>A verifier pod's fixed wall-clock reading, including one legitimately behind the writer pod.</summary>
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        private int _reads;

        public FixedClock(DateTimeOffset now) => _now = now;

        public int Reads => _reads;

        public override DateTimeOffset GetUtcNow()
        {
            Interlocked.Increment(ref _reads);
            return _now;
        }
    }

    /// <summary>A team and the routed destination its placements are expected to sit at.</summary>
    private sealed record Destination(Guid TeamId, Guid ProfileId, string Root);

    /// <summary>One seeded placement, and everything needed to put its object where the driver will look for it.</summary>
    private sealed record Placed(Guid Id, string ObjectKey, string Root);
}
