using System.Data.Common;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts.Providers;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Storage;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Artifacts.Runtime;

/// <summary>
/// What ONE destination that cannot answer costs every other destination's placements.
///
/// <para>The sweep resolved its destination per row, so a bucket that was gone spent a full resolution attempt and
/// returned Inconclusive for every row it held — and because a dead destination's rows are also the oldest rows in the
/// table, the ordering handed it the whole batch. One unmounted volume was enough to stop every HEALTHY destination in
/// the deployment from being checked at all, silently, at a full round trip per row.</para>
///
/// <para>Three independent properties fix that and all three are asserted here: the batch is selected round-robin
/// across distinct destinations, so a destination holding more rows than the batch cannot occupy it; within that, a
/// location already answered for is taken last, so the turns are shared over the rows actually owed an answer; and the
/// first destination-level refusal drops that destination's remaining rows from the pass rather than re-asking each
/// one.</para>
///
/// <para>The last of those is also the one that can rebuild the stall it removes, so its boundary is asserted from
/// both sides: a destination that cannot answer for ITSELF is dropped, and a destination that refuses ONE object goes
/// on being asked about every other. Both sides are asserted for an answer that RAISES as well as for one that returns
/// a code, because an exception separates those two cases no better than a code does — which makes a throw an instance
/// of the rule rather than an exception to it.</para>
///
/// <para>And what a pass drops it must say it dropped — in the summary and in the response the command hands back,
/// because a dropped row that is reported as a destination that answered nothing turns one bucket's outage into a
/// deployment-wide one on the operator's screen.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ArtifactLocationVerifierDestinationFairnessTests : IAsyncLifetime
{
    /// <summary>Older than anything else this suite seeds, so these rows lead every turn of the round-robin and a pass that reached them is not a pass that got lucky.</summary>
    private static readonly TimeSpan Ancient = TimeSpan.FromDays(6000);

    /// <summary>The size every placement here records. Nothing is ever written at the destination, so a HEAD against a live one answers Missing and a demotion is the sweep saying so.</summary>
    private const int ObjectSize = 12;

    /// <summary>
    /// Wide enough that the dead destination's 143 Available and 45 Missing rows each exceed their own share of it,
    /// which is what makes the ORDERING rather than the batch size the thing under test.
    ///
    /// <para>The healthy destination's counts are the other half of that arithmetic. A round-robin batch reaches a
    /// destination's Nth oldest row only after every other destination in the deployment has offered its own Nth, so
    /// nine healthy Available rows need nine turns — which the 120-row Available share affords even with a dozen
    /// unrelated destinations left behind by neighbouring classes, and three Missing rows likewise fit the narrower
    /// 40-row recovery share. Seeding more healthy rows would make this test depend on how many neighbours happen to
    /// have leftovers.</para>
    /// </summary>
    private const int BatchSize = 160;

    /// <summary>
    /// The widest batch a pass will take, for the two-pass test below.
    ///
    /// <para>Its second pass reaches its rows through the narrower recovery share, which the first pass left them in by
    /// demoting them — so the share has to be wide enough that a handful of rows cannot be squeezed out of it by
    /// whatever this suite's other classes happen to have left behind. A ceiling rather than an arithmetic: this test
    /// asserts about rows it names, never about a tally, so a batch larger than it needs costs only time.</para>
    /// </summary>
    private const int WideBatch = 500;

    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _placed = [];

    public ArtifactLocationVerifierDestinationFairnessTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_destination_that_cannot_answer_does_not_consume_the_pass_a_healthy_one_shares()
    {
        var dead = await SeedDestinationAsync();
        var healthy = await SeedDestinationAsync();

        // The dead destination holds more of BOTH populations than its share of the batch, and every one of its rows
        // is older than every one of the healthy destination's — which is what an abandoned destination really looks
        // like, because being oldest is exactly why an ordering by verified_at keeps picking it.
        await PlaceManyAsync(dead, ArtifactLocationState.Available, count: 143, oldest: Ancient + TimeSpan.FromDays(400));
        await PlaceManyAsync(dead, ArtifactLocationState.Missing, count: 45, oldest: Ancient + TimeSpan.FromDays(400));
        var live = await PlaceManyAsync(healthy, ArtifactLocationState.Available, count: 9, oldest: Ancient + TimeSpan.FromDays(100));
        var recovering = await PlaceManyAsync(healthy, ArtifactLocationState.Missing, count: 3, oldest: Ancient + TimeSpan.FromDays(100));
        var before = await VerifiedAtAsync(recovering);
        var opens = new DestinationOpens();

        Vanish(dead);

        (await DemotedAsync(live)).ShouldBe(0, "none of the healthy destination's placements may already be answered, or nothing below is about this pass");

        using var pass = CountingScope(opens);
        var summary = await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(BatchSize, CancellationToken.None);

        // Both shares of the batch, because they are selected by separate queries and a fair ordering in one of them
        // proves nothing about the other. The Available share is where detection happens; the Missing share is where a
        // wrong demotion is undone, and a deployment that lost either to one dead destination lost half the sweep.
        (await DemotedAsync(live)).ShouldBe(live.Count, "every healthy Available placement must be examined even while a dead destination holds fifteen times more rows, all of them older");
        (await VerifiedAtAsync(recovering)).ShouldAllBe(row => row.Value > before[row.Key], "and every healthy Missing placement must be re-asked too — the recovery share is selected by its own query and needs its own fairness");

        opens.For(dead.TeamId).ShouldBe(1, "a destination that failed to answer must be asked ONCE and then dropped for the rest of the pass, not re-asked at a round trip per row");

        // Pinned by an identity rather than by a bound. Every row a pass selects either costs one activation or is
        // dropped without one, so the dropped rows ARE the selected rows minus the activations — and the line above
        // has already established that this test's dead destination spent one activation on many selected rows, so
        // that difference is this test's own and large. A bound of "at least forty-five" says nothing by comparison:
        // the tally is deployment-wide, so any neighbour's leftovers satisfy it, and nothing about it can fail.
        summary.Skipped.ShouldBe(summary.Checked - opens.Total, "and every row it dropped has to be reported as dropped — a pass that asks once and drops forty rows must not report forty destinations that were asked and said nothing");
    }

    [Fact]
    public async Task A_destination_that_disappears_mid_pass_demotes_nothing_behind_it()
    {
        // The corroborating probe is the ONLY thing standing between an unmounted volume and a deployment-wide
        // demotion: every object under a vanished root reads as absent, and a provider cannot tell a deleted object
        // apart from a namespace it can no longer see. So the corroboration is taken per row, inside that row's own
        // lease, immediately before the demotion it licenses — reusing an earlier row's positive answer would demote
        // every row behind the moment the mount went away.
        const int rowOnWhichTheMountVanished = 10;

        var destination = await SeedDestinationAsync();
        var placed = await PlaceManyAsync(destination, ArtifactLocationState.Available, count: 30, oldest: Ancient + TimeSpan.FromDays(300));
        var testifiedFor = placed.Take(rowOnWhichTheMountVanished - 1).ToList();
        var behind = placed.Skip(rowOnWhichTheMountVanished - 1).ToList();
        var before = await VerifiedAtAsync(behind);

        (await DemotedAsync(placed)).ShouldBe(0, "no placement may already be demoted, or neither count below is this pass's work");

        using var pass = VanishingScope(destination, rowOnWhichTheMountVanished);
        await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(BatchSize, CancellationToken.None);

        pass.Resolve<VanishingBroker>().Vanished.ShouldBeTrue("the mount has to have gone away DURING the pass, or nothing below is about a destination that disappeared");
        (await DemotedAsync(testifiedFor)).ShouldBe(testifiedFor.Count, "the rows asked about while the mount was still there must have been demoted on its own testimony, or the pass never got far enough for the rest to matter");

        (await DemotedAsync(behind)).ShouldBe(0, "and from the row that met the vanished mount onwards it must demote NOTHING: their absence is exactly what a destination that cannot answer is no longer able to testify to");

        // Counting demotions cannot see this on its own: the sweep's commonest outcome moves verified_at and leaves the
        // state alone, so a row that had been checked and confirmed is indistinguishable by state from one that was
        // dropped. verified_at is also the sweep's own cursor — moved on a row nobody asked about, the row looks freshly
        // checked and drops out of the ordering for a full cycle, which is a placement quietly stopping being verified.
        (await VerifiedAtAsync(behind)).ShouldAllBe(row => row.Value == before[row.Key], "and none of them may look freshly checked afterwards: a row this pass dropped unasked has to keep the stale verified_at that is the honest record of when it was last actually known");
    }

    [Fact]
    public async Task An_object_its_destination_refuses_costs_that_object_and_no_other_pass_after_pass()
    {
        // Forbidden is an answer about ONE key. A destination hands it back for an object whose ACL drifted, a prefix
        // somebody revoked, a path the provider stopped supporting — and serves every other object underneath
        // perfectly well. Read as "this destination did not answer", it drops every sibling from the pass; and because
        // a row nothing was established about never moves its verified_at, that row leads its destination's ranking
        // again next hour and drops them again. A per-object condition does not heal, so this is not one cycle: it is
        // the destination's entire placement set going unverified for as long as the ACL stays wrong — the permanent
        // stall the round robin exists to remove, rebuilt by the containment that was meant to help.
        var destination = await SeedDestinationAsync();
        var placed = await PlaceManyAsync(destination, ArtifactLocationState.Available, count: 8, oldest: Ancient + TimeSpan.FromDays(200));
        var refused = placed[0];
        var siblings = placed.Skip(1).ToList();
        var forbidden = new ForbiddenObject(ObjectKeyOf(refused));
        var refusedBefore = (await VerifiedAtAsync([refused]))[refused];

        (await DemotedAsync(siblings)).ShouldBe(0, "no sibling may already be answered, or neither pass below is about this destination's placements");

        using (var pass = RefusingScope(forbidden))
            await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(WideBatch, CancellationToken.None);

        forbidden.Refusals.ShouldBe(1, "the pass has to have met the refusal on the oldest row of this destination, or everything below is vacuous");
        (await DemotedAsync(siblings)).ShouldBe(siblings.Count, "one object the destination refuses must cost exactly that object: every placement beside it was answerable, and a refusal about a key says nothing whatever about a different key");
        (await VerifiedAtAsync([refused]))[refused].ShouldBe(refusedBefore, "and the refused row itself must be left as it was, including its stale verified_at — nothing was established about the object");

        // The next hour: the siblings are due again, and the refused row is exactly where the pass left it — at the
        // front of its destination's ranking, because a row that answered nothing never moved its cursor.
        await ReAgeAsync(siblings);
        var before = await VerifiedAtAsync(siblings);

        using (var next = RefusingScope(forbidden))
            await next.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(WideBatch, CancellationToken.None);

        forbidden.Refusals.ShouldBe(2, "the refusal has to have been met a second time — a per-object condition does not heal, which is exactly what makes silencing on it permanent rather than momentary");
        (await VerifiedAtAsync(siblings)).ShouldAllBe(row => row.Value > before[row.Key], "and every sibling must be examined AGAIN: a refusal that costs its destination's placements one pass costs them every pass, forever");
    }

    [Fact]
    public async Task A_destination_that_refuses_every_object_is_asked_once_rather_than_once_per_row()
    {
        // Not every destination-wide fault answers Missing. A credential that expired, a role somebody revoked, a
        // bucket policy that changed overnight — each of them answers Forbidden for EVERY object underneath, and a
        // pass that asks the destination about itself only when an object reads as absent never asks any of them.
        // Never asked, never remembered: the destination then costs a full activation and a round trip on every row it
        // holds, in this pass and in every pass after it, which is the permanent cost the containment exists to
        // remove — surviving inside the containment, on every fault that does not happen to say Missing.
        var denied = await SeedDestinationAsync();
        var healthy = await SeedDestinationAsync();
        var refused = await PlaceManyAsync(denied, ArtifactLocationState.Available, count: 8, oldest: Ancient + TimeSpan.FromDays(260));
        var live = await PlaceManyAsync(healthy, ArtifactLocationState.Available, count: 8, oldest: Ancient + TimeSpan.FromDays(250));
        var before = await VerifiedAtAsync(refused);
        var credential = new ExpiredCredential();
        var opens = new DestinationOpens();

        (await DemotedAsync(live)).ShouldBe(0, "none of the healthy destination's placements may already be answered, or the depth this pass reached says nothing");

        using var pass = DenyingScope(denied, credential, opens);
        await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(WideBatch, CancellationToken.None);

        // The healthy destination is the depth gauge. Its rows are YOUNGER than the denied destination's, so a batch
        // that reached its eighth turn had already offered the denied destination all eight of its own — without which
        // "refused once" would be satisfied just as well by a pass that only ever selected one of those rows.
        (await DemotedAsync(live)).ShouldBe(live.Count, "the pass has to have gone eight turns deep, or one refusal proves nothing about the seven rows behind it");

        credential.Probes.ShouldBe(1, "a destination that answered about no object at all has to be asked about ITSELF: the probe is the only thing that tells one expired credential apart from one drifted key, and only its answer may drop the rows behind");
        credential.Refusals.ShouldBe(1, "and having answered nothing, it must be dropped for the rest of the pass — a refusal per row IS the round trip per row this change exists to remove");
        opens.For(denied.TeamId).ShouldBe(1, "one activation for the whole destination, not one for every placement pinned to it");

        (await VerifiedAtAsync(refused)).ShouldAllBe(row => row.Value == before[row.Key], "and every row it dropped keeps the stale verified_at that is the honest record of when it was last actually known");
    }

    [Fact]
    public async Task A_destination_that_raises_as_its_handle_is_released_is_still_dropped_after_one_round_trip()
    {
        // Releasing the handle is the last thing a row does with its destination, and it is a call that can raise on
        // its own: a descriptor whose mount was pulled out from under it, a socket whose close resets, a client whose
        // shutdown times out. By then the destination has ALREADY answered the only question whose answer may drop the
        // rows behind — it said it cannot answer — so the throw establishes nothing new and must cost nothing. Filed a
        // line too late, it costs everything: the exception carries the row out past the filing, and the destination
        // this pass has just proved cannot answer is asked again, at a full activation and a round trip on every row it
        // holds. That is the exact cost this change exists to remove, restored by the one path out of a row's work that
        // runs AFTER the answer exists and BEFORE it is written down.
        var unreleasable = await SeedDestinationAsync();
        var stranded = await PlaceManyAsync(unreleasable, ArtifactLocationState.Available, count: 8, oldest: Ancient + TimeSpan.FromDays(220));
        var before = await VerifiedAtAsync(stranded);
        var credential = new ExpiredCredential();
        var opens = new DestinationOpens();

        using var pass = UnreleasableScope(unreleasable, credential, opens);
        var summary = await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(WideBatch, CancellationToken.None);

        credential.Probes.ShouldBe(1, "the destination has to have been asked about ITSELF exactly once: that answer is what proves it cannot answer, and a throw on the way out of the row does not unprove it");
        opens.For(unreleasable.TeamId).ShouldBe(1, "and having answered nothing, it must be dropped for the rest of the pass however the row it answered on ended — a release that raises is not a destination that has recovered");

        // The same identity the dead-destination test is pinned by, and for the same reason: every row a pass selects
        // either costs one activation or is dropped without one, so the dropped rows ARE the selected rows minus the
        // activations. The line above has already established that this destination spent one activation while holding
        // eight rows, so the difference this assertion measures is this test's own.
        summary.Skipped.ShouldBe(summary.Checked - opens.Total, "and every row behind it has to be REPORTED as dropped — a row the pass never asked about is not one its destination answered nothing for");

        (await VerifiedAtAsync(stranded)).ShouldAllBe(row => row.Value == before[row.Key], "while every one of them keeps the stale verified_at that is the honest record of when it was last actually known");
    }

    [Fact]
    public async Task A_destination_that_raises_instead_of_answering_is_asked_once_rather_than_once_per_row()
    {
        // Not every fault comes back as a code. A mount whose I/O fails, an endpoint the client cannot reach, a
        // response it cannot parse — each of them RAISES, on every request, for every object underneath. That is a
        // destination-level fault by any reading, and an exception says so no more clearly than an error code does:
        // one driver raising about one object and one raising about all of them arrive as the same throw, which is
        // precisely the question the probe exists to settle. Left outside the rule the codes live under, a throwing
        // destination costs a full activation and a round trip on every row it holds, in every pass, forever.
        var raising = await SeedDestinationAsync();
        var healthy = await SeedDestinationAsync();
        var unreachable = await PlaceManyAsync(raising, ArtifactLocationState.Available, count: 8, oldest: Ancient + TimeSpan.FromDays(240));
        var live = await PlaceManyAsync(healthy, ArtifactLocationState.Available, count: 8, oldest: Ancient + TimeSpan.FromDays(230));
        var before = await VerifiedAtAsync(unreachable);
        var fault = RaisingDestination.AboutEverything();
        var opens = new DestinationOpens();

        (await DemotedAsync(live)).ShouldBe(0, "none of the healthy destination's placements may already be answered, or the depth this pass reached says nothing");

        using var pass = RaisingScope(raising, fault, opens);
        await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(WideBatch, CancellationToken.None);

        // The healthy destination is the depth gauge, exactly as above: its rows are YOUNGER than the raising
        // destination's, so a batch that reached its eighth turn had already offered the raising destination all eight
        // of its own — without which "threw once" would be satisfied by a pass that only ever selected one of them.
        (await DemotedAsync(live)).ShouldBe(live.Count, "the pass has to have gone eight turns deep, or one throw proves nothing about the seven rows behind it");

        fault.Probes.ShouldBe(1, "a destination that raised about the object has to be asked about ITSELF: nothing in an exception separates one unreadable key from a whole unreachable mount, and the probe is the only thing that can");
        fault.Throws.ShouldBe(1, "and having answered nothing about itself either, it must be dropped for the rest of the pass — a throw per row IS the round trip per row this change exists to remove");
        opens.For(raising.TeamId).ShouldBe(1, "one activation for the whole destination, not one for every placement pinned to it");

        (await VerifiedAtAsync(unreachable)).ShouldAllBe(row => row.Value == before[row.Key], "and every row it dropped keeps the stale verified_at that is the honest record of when it was last actually known");
    }

    [Fact]
    public async Task An_object_its_destination_raises_about_costs_that_object_and_no_other()
    {
        // The other side of the same boundary, and the reason a throw may not simply be filed against the destination
        // either. One key whose metadata the client cannot decode, one path it cannot build a request for, one socket
        // reset mid-HEAD — each raises about ONE object at a destination serving every other one perfectly. Read as
        // "this destination did not answer", it drops every sibling from the pass; and because a row nothing was
        // established about never moves its verified_at, that row leads the destination's ranking again next hour and
        // drops them again. Only the probe separates the two, so the throw gets the same probe the codes get.
        var destination = await SeedDestinationAsync();
        var placed = await PlaceManyAsync(destination, ArtifactLocationState.Available, count: 8, oldest: Ancient + TimeSpan.FromDays(210));
        var raised = placed[0];
        var siblings = placed.Skip(1).ToList();
        var fault = RaisingDestination.AboutOneKey(ObjectKeyOf(raised));
        var raisedBefore = (await VerifiedAtAsync([raised]))[raised];
        var opens = new DestinationOpens();

        (await DemotedAsync(siblings)).ShouldBe(0, "no sibling may already be answered, or nothing below is about this pass");

        using var pass = RaisingScope(destination, fault, opens);
        await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(WideBatch, CancellationToken.None);

        fault.Throws.ShouldBe(1, "the pass has to have met the throw on the oldest row of this destination, or everything below is vacuous");
        fault.Probes.ShouldBe(placed.Count, "and the row that raised has to be corroborated exactly as the seven that answered a code were: eight unsettled answers, eight probes, or the throw is being let out of the rule its siblings live under");

        (await DemotedAsync(siblings)).ShouldBe(siblings.Count, "a probe that says the destination is fine leaves the throw meaning what it says — one object — so every sibling must still be examined");
        opens.For(destination.TeamId).ShouldBe(placed.Count, "and each of them still cost its own activation: a destination the probe cleared is not one the pass may drop");
        (await VerifiedAtAsync([raised]))[raised].ShouldBe(raisedBefore, "while the row that raised is left as it was, including its stale verified_at — nothing was established about the object");
    }

    [Fact]
    public async Task A_turn_at_a_destination_that_was_just_answered_for_comes_after_one_that_has_waited()
    {
        // A turn each, and nothing else, shares the batch over rows rather than over work owed. Every destination's
        // FIRST row outranks every destination's second, so a destination holding one placement checked minutes ago
        // takes a slot ahead of another destination's second-oldest row however many years that one has waited — and
        // it does so on every pass, because being re-checked is what keeps it holding a first turn. A deployment of
        // many small destinations and one large one spends its budget re-asking the answered and advances the large
        // one a row an hour.
        var waiting = await SeedDestinationAsync();
        var justAnswered = await SeedDestinationAsync();
        var overdue = await PlaceManyAsync(waiting, ArtifactLocationState.Missing, count: 2, oldest: Ancient + TimeSpan.FromDays(700));
        await PlaceManyAsync(justAnswered, ArtifactLocationState.Missing, count: 1, oldest: TimeSpan.FromMinutes(5));
        var opens = new DestinationOpens();

        using var pass = CountingScope(opens);
        await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(WideBatch, CancellationToken.None);

        // Read off the order the pass actually went in, not off the batch's edge: a shared database has no edge a test
        // can place, but the sequence of activations is the batch's ordering itself and two destinations' places in it
        // are the same however many neighbours sit between them. Only the overdue rows are pinned, and they can be:
        // they are the oldest rows in the deployment, so they lead the ordering whatever else is in it.
        //
        // The FRESH row's own reach deliberately is not. Sorting it behind every owed row in the deployment is the
        // very thing under test, so whether a batch of this width still has room to reach it is a count of rows this
        // test does not own — a neighbouring class's leftovers decide it. Not reached at all is the strongest form of
        // not taken first, and belongs on the same side of the property as reached last.
        opens.For(waiting.TeamId).ShouldBe(overdue.Count, "both overdue placements have to have been reached, or their place in the ordering says nothing");

        opens.FirstOpenOf(justAnswered.TeamId).ShouldBeGreaterThan(opens.LastOpenOf(waiting.TeamId),
            "a placement answered for minutes ago must be taken only once every placement still owed an answer has been: fairness over turns alone shares the batch by row count, and the rows that need it are the ones nobody has answered for");
    }

    [Fact]
    public async Task The_response_the_command_returns_accounts_for_the_rows_the_pass_dropped()
    {
        // The pass counts what it dropped; the command's response is the only structured place an operator reads that
        // count, and it did not carry the field at all. One destination going quiet answers for exactly one row and
        // drops the rest, so a response without it reports a batch of a hundred as a pass that examined a handful —
        // and does so most wrongly in precisely the scenario this whole change is about.
        var dead = await SeedDestinationAsync();
        await PlaceManyAsync(dead, ArtifactLocationState.Available, count: 30, oldest: Ancient + TimeSpan.FromDays(600));
        var opens = new DestinationOpens();

        Vanish(dead);

        using var pass = CountingScope(opens);
        var response = await pass.Resolve<IMediator>().Send(new VerifyStaleArtifactLocationsCommand());

        opens.For(dead.TeamId).ShouldBe(1, "the vanished destination has to have been asked once and then dropped, or there is nothing here for a response to under-report");
        response.Skipped.ShouldBe(response.Checked - opens.Total, "and the response has to account for every row the pass selected: one that cost no activation was dropped, and a response that cannot say so hides most of the batch");
    }

    [Fact]
    public async Task The_batch_ordering_reaches_its_rows_by_index_rather_than_by_scanning_every_placement()
    {
        // The sweep is deployment-wide and deliberately not filtered by team, so ix_artifact_location_state_verified —
        // which leads on team_id — cannot serve it at all. Without an index whose leading column is the state, both
        // shares of every hourly batch are a full scan of every placement in the deployment plus a sort.
        var destination = await SeedDestinationAsync();
        await PlaceManyAsync(destination, ArtifactLocationState.Missing, count: 1, oldest: Ancient + TimeSpan.FromDays(500));
        var ordering = new CapturedOrdering();

        using (var pass = CapturingScope(ordering))
            await pass.Resolve<IArtifactLocationVerifier>().VerifyStaleAsync(batchSize: 1, CancellationToken.None);

        ordering.CommandText.ShouldNotBeNull("the pass has to have issued its batch-ordering query, or there is no plan to pin");

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // A test database holds too few rows for a cost-based planner to ever prefer an index, so the knob asks the
        // only question that is about the schema rather than about the row count: given that scanning is not on offer,
        // can this query be answered from an index at all. Without the index below it still cannot — the planner falls
        // back to the sequential scan the assertion forbids.
        await using (var settings = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, transaction))
            await settings.ExecuteNonQueryAsync();

        var plan = await ExplainAsync(connection, transaction, ordering);

        plan.ShouldContain("ix_artifact_location_state_destination_verified", customMessage: "the ordering has to be served by the index built for it — any other index means the round-robin ranking is sorting the whole table by hand");
        plan.ShouldNotContain("Seq Scan on artifact_location", customMessage: "and must never fall back to reading every placement in the deployment once an hour");

        await transaction.RollbackAsync();
    }

    // ─── World ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts a counter in front of the real broker, so a test can say what a dropped destination cost measured in round
    /// trips.
    ///
    /// <para>The tally is owned by the test rather than by the container: a pass dispatched through the mediator
    /// resolves its handler in a scope of the container's choosing, and a per-scope decorator would then be counting
    /// into an instance the test never sees.</para>
    /// </summary>
    private ILifetimeScope CountingScope(DestinationOpens opens) => _fixture.BeginScope(builder => builder
        .Register<IStorageRuntimeDriverBroker>(context => new CountingBroker(context.Resolve<StorageRuntimeDriverBroker>(), opens))
        .InstancePerLifetimeScope());

    /// <summary>A scope whose destinations refuse ONE object key and serve every other one exactly as they always did.</summary>
    private ILifetimeScope RefusingScope(ForbiddenObject forbidden) => _fixture.BeginScope(builder => builder
        .Register<IStorageRuntimeDriverBroker>(context => new RefusingBroker(context.Resolve<StorageRuntimeDriverBroker>(), forbidden))
        .InstancePerLifetimeScope());

    /// <summary>A scope in which ONE destination's credential has expired — it refuses every object underneath it and cannot answer for itself either — while every activation is still counted.</summary>
    private ILifetimeScope DenyingScope(Destination denied, ExpiredCredential credential, DestinationOpens opens) => _fixture.BeginScope(builder => builder
        .Register<IStorageRuntimeDriverBroker>(context => new DenyingBroker(new CountingBroker(context.Resolve<StorageRuntimeDriverBroker>(), opens), denied, credential))
        .InstancePerLifetimeScope());

    /// <summary>
    /// A scope in which ONE destination can neither answer nor be let go of: it refuses every object and says it is
    /// unavailable about itself, and then raises as its handle is released.
    ///
    /// <para>Layered over the denying one rather than written afresh, because the fault under test is ONLY the
    /// release. Everything before it — the activation, the refusal, the probe — has to be the behaviour the denying
    /// test already pins, or the two tests would be about different destinations and neither would isolate the
    /// throw.</para>
    /// </summary>
    private ILifetimeScope UnreleasableScope(Destination destination, ExpiredCredential credential, DestinationOpens opens) => _fixture.BeginScope(builder => builder
        .Register<IStorageRuntimeDriverBroker>(context => new UnreleasableBroker(new DenyingBroker(new CountingBroker(context.Resolve<StorageRuntimeDriverBroker>(), opens), destination, credential), destination))
        .InstancePerLifetimeScope());

    /// <summary>A scope in which ONE destination raises instead of answering — about a single object, or about everything it is asked including itself — while every activation is still counted.</summary>
    private ILifetimeScope RaisingScope(Destination destination, RaisingDestination fault, DestinationOpens opens) => _fixture.BeginScope(builder => builder
        .Register<IStorageRuntimeDriverBroker>(context => new RaisingBroker(new CountingBroker(context.Resolve<StorageRuntimeDriverBroker>(), opens), destination, fault))
        .InstancePerLifetimeScope());

    /// <summary>A scope whose destination stops existing part-way through the pass, as it opens the Nth row that lives there.</summary>
    private ILifetimeScope VanishingScope(Destination destination, int afterRows) => _fixture.BeginScope(builder => builder
        .Register(context => new VanishingBroker(context.Resolve<StorageRuntimeDriverBroker>(), destination, afterRows))
        .AsSelf().As<IStorageRuntimeDriverBroker>()
        .InstancePerLifetimeScope());

    /// <summary>
    /// A scope whose verifier — and only the verifier — reports the exact SQL it ordered its batch with.
    ///
    /// <para>Captured rather than copied: a mirror of the query in this file would go on asserting that an index serves
    /// a shape production has since stopped using, which is the one failure mode an index test must not have.</para>
    /// </summary>
    private ILifetimeScope CapturingScope(CapturedOrdering ordering) => _fixture.BeginScope(builder => builder
        .Register<IArtifactLocationVerifier>(context => new ArtifactLocationVerifier(
            new DbContextOptionsBuilder<CodeSpaceDbContext>(context.Resolve<DbContextOptions<CodeSpaceDbContext>>()).AddInterceptors(ordering).Options,
            context.Resolve<IStorageRuntimeDriverBroker>(), context.Resolve<TimeProvider>(), context.Resolve<ILogger<ArtifactLocationVerifier>>()))
        .InstancePerLifetimeScope());

    private static async Task<string> ExplainAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CapturedOrdering ordering)
    {
        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + ordering.CommandText, connection, transaction);

        foreach (var (name, value) in ordering.Parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));

        return string.Join('\n', lines);
    }

    /// <summary>Takes the whole destination away — an unmounted volume, a detached disk — so its objects read as absent and it can no longer testify that any of them was deleted.</summary>
    private static void Vanish(Destination destination) => Directory.Delete(destination.Root, recursive: true);

    /// <summary>How many of these placements the sweep demoted. Their objects were never written, so a row a live destination answered about comes back Missing.</summary>
    private async Task<int> DemotedAsync(IReadOnlyCollection<Guid> placed)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .CountAsync(location => placed.Contains(location.Id) && location.State == ArtifactLocationState.Missing);
    }

    private async Task<Dictionary<Guid, DateTimeOffset?>> VerifiedAtAsync(IReadOnlyCollection<Guid> placed)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().ArtifactLocation.AsNoTracking()
            .Where(location => placed.Contains(location.Id))
            .ToDictionaryAsync(location => location.Id, location => location.VerifiedAt);
    }

    /// <summary>
    /// Puts these placements back where the seed left them, which is what the next hour looks like to a row nothing has
    /// answered for since.
    ///
    /// <para>Their <c>created_date</c> is the oldest instant the schema will accept for <c>verified_at</c>, and the
    /// revision has to advance carrying a byte-identical ledger entry, because the schema admits no other way to move a
    /// location at all.</para>
    /// </summary>
    private async Task ReAgeAsync(IReadOnlyCollection<Guid> placed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var rows = await db.ArtifactLocation.Where(location => placed.Contains(location.Id)).ToListAsync();

        foreach (var row in rows)
        {
            row.VerifiedAt = row.CreatedDate;
            row.Revision++;
            row.LastModifiedDate = DateTimeOffset.UtcNow;
            db.ArtifactLocationEvent.Add(Snapshot(row));
        }

        await db.SaveChangesAsync();
    }

    private async Task<Destination> SeedDestinationAsync()
    {
        var (teamId, actorId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var routed = await RoutedArtifactSeed.RouteTeamAsync(_fixture, teamId, actorId);

        using var scope = _fixture.BeginScope();
        var revisionId = await scope.Resolve<CodeSpaceDbContext>().StorageProfileRevision.AsNoTracking()
            .Where(revision => revision.StorageProfileId == routed.ProfileId)
            .OrderByDescending(revision => revision.Revision).Select(revision => revision.Id).FirstAsync();

        return new Destination(teamId, revisionId, routed.Root);
    }

    /// <summary>
    /// A run of placements on one destination, oldest first, seeded through ONE context.
    ///
    /// <para>Two hundred rows one scope at a time is minutes of test time bought for nothing: every row here is an
    /// ordinary placement at the revision every ledger starts on, and the schema's only demand is that each carries a
    /// byte-identical event at that revision, which one SaveChanges satisfies exactly as two hundred would.</para>
    /// </summary>
    private async Task<IReadOnlyList<Guid>> PlaceManyAsync(Destination destination, ArtifactLocationState state, int count, TimeSpan oldest)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var observed = DateTimeOffset.UtcNow - oldest;
        var placed = new List<Guid>();

        foreach (var index in Enumerable.Range(0, count)) placed.Add(Place(db, destination, state, observed + TimeSpan.FromMinutes(index)));

        await db.SaveChangesAsync();
        _placed.AddRange(placed);

        return placed;
    }

    private static Guid Place(CodeSpaceDbContext db, Destination destination, ArtifactLocationState state, DateTimeOffset observed)
    {
        var locationId = Guid.NewGuid();
        var objectId = Guid.NewGuid();
        var checksum = System.Security.Cryptography.SHA256.HashData(objectId.ToByteArray());

        db.ArtifactObject.Add(new ArtifactObject { Id = objectId, TeamId = destination.TeamId, Digest = checksum, SizeBytes = ObjectSize, CreatedDate = observed });

        var location = new ArtifactLocation
        {
            Id = locationId, TeamId = destination.TeamId, ArtifactObjectId = objectId, StorageProfileRevisionId = destination.RevisionId,
            Locator = "local://destination-fairness", ObjectKey = ObjectKeyOf(locationId), State = state, VerifiedAt = observed,
            Revision = 1, CreatedDate = observed, LastModifiedDate = observed,
            ObservedSizeBytes = ObjectSize, ProviderChecksumAlgorithm = "Sha256", ProviderChecksum = checksum,
        };
        db.ArtifactLocation.Add(location);
        db.ArtifactLocationEvent.Add(Snapshot(location));

        return locationId;
    }

    /// <summary>The key a placement is written under, so a test can name one object to a driver without carrying the row around.</summary>
    private static string ObjectKeyOf(Guid locationId) => $"objects/{locationId:N}";

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

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Takes every row this class placed permanently out of the sweep.
    ///
    /// <para>They are seeded older than anything else in the suite so they lead every turn of the round-robin, which is
    /// the only way to be sure the pass under test actually reached them — and that makes them the front of every LATER
    /// test's batch too. Two hundred of them is more than any share holds, so a neighbour's one row would never get a
    /// slot. <c>Deleted</c> is terminal and no pass ever selects it. Best-effort, and on the failure path too: a
    /// failing test that leaks these breaks its neighbours rather than itself.</para>
    /// </summary>
    public async Task DisposeAsync()
    {
        try
        {
            using var scope = _fixture.BeginScope();
            var db = scope.Resolve<CodeSpaceDbContext>();
            var rows = await db.ArtifactLocation.Where(location => _placed.Contains(location.Id)).ToListAsync();

            foreach (var row in rows)
            {
                row.State = ArtifactLocationState.Deleted;
                row.Revision++;
                row.LastModifiedDate = DateTimeOffset.UtcNow;
                db.ArtifactLocationEvent.Add(Snapshot(row));
            }

            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) { }
        catch (DbException) { }
    }

    private sealed record Destination(Guid TeamId, Guid RevisionId, string Root);

    /// <summary>
    /// How often a pass asked the broker for a destination, per team and in total.
    ///
    /// <para>The total is what makes a tally of dropped rows checkable: every row a pass selects either costs exactly
    /// one activation or is dropped without one, so the rows it never asked about are the rows it selected minus the
    /// activations it spent. A count the pass reports that disagrees with that is a count of something else.</para>
    /// </summary>
    private sealed class DestinationOpens
    {
        private readonly Dictionary<Guid, int> _perTeam = [];
        private readonly List<Guid> _sequence = [];

        public int Total { get; private set; }

        public int For(Guid teamId) => _perTeam.GetValueOrDefault(teamId);

        /// <summary>
        /// Where in the pass's order this destination was FIRST activated — the batch's own ordering, read off what
        /// the pass actually did with it — or past the end of the pass, when it was never activated at all.
        ///
        /// <para>A destination the batch never reached is behind every destination it did. Saying so is what lets a
        /// test assert that one destination was taken after another without ALSO having to assert that a
        /// deployment-wide batch had room to get that far, which is a count no test owns.</para>
        /// </summary>
        public int FirstOpenOf(Guid teamId) => _sequence.Contains(teamId) ? _sequence.IndexOf(teamId) : int.MaxValue;

        /// <summary>Where in the pass's order this destination was LAST activated.</summary>
        public int LastOpenOf(Guid teamId) => _sequence.LastIndexOf(teamId);

        public void Opened(Guid teamId)
        {
            _perTeam[teamId] = For(teamId) + 1;
            _sequence.Add(teamId);
            Total++;
        }
    }

    /// <summary>Counts how often a pass asked the broker for each team's destination, which is what a dropped destination saves measured in round trips.</summary>
    private sealed class CountingBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;
        private readonly DestinationOpens _opens;

        public CountingBroker(IStorageRuntimeDriverBroker inner, DestinationOpens opens)
        {
            _inner = inner;
            _opens = opens;
        }

        public ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            _opens.Opened(request.TeamId);

            return _inner.OpenAsync(request, cancellationToken);
        }
    }

    /// <summary>
    /// One object key its destination will not answer for, however often it is asked — a drifted ACL, a path the
    /// provider stopped supporting, a prefix somebody revoked.
    ///
    /// <para>The destination around it is entirely healthy, which is the whole point: the refusal is about the object,
    /// so nothing it establishes is true of the destination's other placements. It also never heals, because a per-object
    /// condition does not, and that is what turns "skip the rest of this pass" into "skip them for good".</para>
    /// </summary>
    private sealed class ForbiddenObject
    {
        public ForbiddenObject(string objectKey) => ObjectKey = objectKey;

        public string ObjectKey { get; }

        public int Refusals { get; private set; }

        public void Refused() => Refusals++;
    }

    /// <summary>Hands out the real driver, wrapped so one key answers Forbidden.</summary>
    private sealed class RefusingBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;
        private readonly ForbiddenObject _forbidden;

        public RefusingBroker(IStorageRuntimeDriverBroker inner, ForbiddenObject forbidden)
        {
            _inner = inner;
            _forbidden = forbidden;
        }

        public async ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            var resolution = await _inner.OpenAsync(request, cancellationToken);

            return resolution is StorageRuntimeDriverResolution.Ready ready
                ? new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(new RefusingDriver(ready.Lease, _forbidden)))
                : resolution;
        }
    }

    /// <summary>The real driver with one key taken away from it. Disposal is forwarded to the lease it came out of, so the credential material behind it is released exactly as production releases it.</summary>
    private sealed class RefusingDriver : IArtifactStorageDriver
    {
        private readonly StorageRuntimeDriverLease _lease;
        private readonly IArtifactStorageDriver _inner;
        private readonly ForbiddenObject _forbidden;

        public RefusingDriver(StorageRuntimeDriverLease lease, ForbiddenObject forbidden)
        {
            _lease = lease;
            _inner = lease.Driver;
            _forbidden = forbidden;
        }

        public StorageProviderCapabilities Capabilities => _inner.Capabilities;

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
        {
            if (request.ObjectKey != _forbidden.ObjectKey) return _inner.HeadAsync(request, cancellationToken);

            _forbidden.Refused();

            return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Forbidden, $"Object '{request.ObjectKey}' is not readable with this credential.")));
        }

        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => _inner.PutAsync(request, cancellationToken);

        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => _inner.OpenReadAsync(request, cancellationToken);

        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => _inner.DeleteAsync(request, cancellationToken);

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => _inner.ProbeAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }

    /// <summary>
    /// A destination whose credential has expired — a role revoked, a bucket policy rewritten overnight — counted from
    /// both of the questions a pass can put to it.
    ///
    /// <para>The two counts are what tell the two faults apart. Every object underneath answers the SAME refusal, so a
    /// refusal per row is the round trip per row this change exists to remove; and none of those refusals is
    /// <c>Missing</c>, so the only question whose answer may drop the rows behind is the one asked of the destination
    /// itself.</para>
    /// </summary>
    private sealed class ExpiredCredential
    {
        /// <summary>How often the destination was asked about ITSELF.</summary>
        public int Probes { get; private set; }

        /// <summary>How often it refused an object.</summary>
        public int Refusals { get; private set; }

        public void Probed() => Probes++;

        public void Refused() => Refusals++;
    }

    /// <summary>Hands out the real driver everywhere except at one destination, where the credential behind it no longer works.</summary>
    private sealed class DenyingBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;
        private readonly Destination _denied;
        private readonly ExpiredCredential _credential;

        public DenyingBroker(IStorageRuntimeDriverBroker inner, Destination denied, ExpiredCredential credential)
        {
            _inner = inner;
            _denied = denied;
            _credential = credential;
        }

        public async ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            var resolution = await _inner.OpenAsync(request, cancellationToken);

            // The broker resolves this destination perfectly well, which is the point: an expired credential is not a
            // profile nobody can find, it is material the PROVIDER rejects, and nothing discovers that until something
            // is actually asked of it. A pass that never asks pays the full activation anyway, on every row.
            return request.TeamId == _denied.TeamId && resolution is StorageRuntimeDriverResolution.Ready ready
                ? new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(new DenyingDriver(ready.Lease, _credential)))
                : resolution;
        }
    }

    /// <summary>The driver an expired credential leaves behind: Forbidden about every object, and unavailable about itself. Disposal is forwarded to the lease it came out of, exactly as production releases one.</summary>
    private sealed class DenyingDriver : IArtifactStorageDriver
    {
        private readonly StorageRuntimeDriverLease _lease;
        private readonly ExpiredCredential _credential;

        public DenyingDriver(StorageRuntimeDriverLease lease, ExpiredCredential credential)
        {
            _lease = lease;
            _credential = credential;
        }

        public StorageProviderCapabilities Capabilities => _lease.Driver.Capabilities;

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
        {
            _credential.Refused();

            return ValueTask.FromResult(ArtifactStorageHeadResult.Failed(new ArtifactStorageError(ArtifactStorageErrorCode.Forbidden, $"Object '{request.ObjectKey}' is not readable with this credential.")));
        }

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken)
        {
            _credential.Probed();

            return ValueTask.FromResult(new ArtifactStorageProbeResult { Status = ArtifactStorageProbeStatus.Unavailable, Latency = TimeSpan.Zero, Error = new ArtifactStorageError(ArtifactStorageErrorCode.Forbidden, "This credential can no longer reach the destination.") });
        }

        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A verification pass writes nothing at a destination.");

        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A verification pass reads no object bodies.");

        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A verification pass deletes nothing.");

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }

    /// <summary>Hands out whatever driver the broker underneath resolved, behind a handle that raises when ONE destination's lease is closed.</summary>
    private sealed class UnreleasableBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;
        private readonly Destination _destination;

        public UnreleasableBroker(IStorageRuntimeDriverBroker inner, Destination destination)
        {
            _inner = inner;
            _destination = destination;
        }

        public async ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            var resolution = await _inner.OpenAsync(request, cancellationToken);

            return request.TeamId == _destination.TeamId && resolution is StorageRuntimeDriverResolution.Ready ready
                ? new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(new UnreleasableDriver(ready.Lease)))
                : resolution;
        }
    }

    /// <summary>
    /// The driver behind a handle nobody gets back: it answers exactly as the one it wraps, and then raises as it is
    /// released — after the lease it came out of has been released properly, so the credential material behind it is
    /// still let go of exactly as production lets go of it.
    /// </summary>
    private sealed class UnreleasableDriver : IArtifactStorageDriver
    {
        private readonly StorageRuntimeDriverLease _lease;

        public UnreleasableDriver(StorageRuntimeDriverLease lease) => _lease = lease;

        public StorageProviderCapabilities Capabilities => _lease.Driver.Capabilities;

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken) => _lease.Driver.HeadAsync(request, cancellationToken);

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken) => _lease.Driver.ProbeAsync(request, cancellationToken);

        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A verification pass writes nothing at a destination.");

        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A verification pass reads no object bodies.");

        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A verification pass deletes nothing.");

        public async ValueTask DisposeAsync()
        {
            await _lease.DisposeAsync();

            throw new IOException("The destination's handle could not be released.");
        }
    }

    /// <summary>
    /// A destination that RAISES instead of answering — about one named key, or about everything it is asked, itself
    /// included.
    ///
    /// <para>Those two are what the probe exists to tell apart, and nothing in an exception separates them: a client
    /// that cannot build a request for one path and a mount whose every I/O fails arrive as the same throw. So both
    /// counts are kept — the objects it raised about, and how often it was asked about ITSELF, which is the only
    /// answer allowed to drop the rows behind.</para>
    /// </summary>
    private sealed class RaisingDestination
    {
        private readonly string? _objectKey;

        private RaisingDestination(string? objectKey) => _objectKey = objectKey;

        /// <summary>Raises about one key, and answers for itself and for every other key exactly as the real destination does.</summary>
        public static RaisingDestination AboutOneKey(string objectKey) => new(objectKey);

        /// <summary>Raises about everything it is asked, itself included — a mount whose every I/O fails.</summary>
        public static RaisingDestination AboutEverything() => new(null);

        /// <summary>How many objects it raised about.</summary>
        public int Throws { get; private set; }

        /// <summary>How often it was asked about ITSELF, whether or not it managed to answer.</summary>
        public int Probes { get; private set; }

        public bool RaisesAbout(string objectKey) => _objectKey == null || _objectKey == objectKey;

        /// <summary>Whether it can still answer about itself, which is what separates one unreadable key from a destination that has stopped answering at all.</summary>
        public bool AnswersForItself => _objectKey != null;

        public void Threw() => Throws++;

        public void Probed() => Probes++;
    }

    /// <summary>Hands out the real driver everywhere except at one destination, where it raises rather than answering.</summary>
    private sealed class RaisingBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;
        private readonly Destination _destination;
        private readonly RaisingDestination _fault;

        public RaisingBroker(IStorageRuntimeDriverBroker inner, Destination destination, RaisingDestination fault)
        {
            _inner = inner;
            _destination = destination;
            _fault = fault;
        }

        public async ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            var resolution = await _inner.OpenAsync(request, cancellationToken);

            // The broker resolves this destination perfectly well: a driver that throws is not a profile nobody can
            // find, it is one nothing discovers until something is actually asked of it — so the activation is spent
            // in full before the first exception exists.
            return request.TeamId == _destination.TeamId && resolution is StorageRuntimeDriverResolution.Ready ready
                ? new StorageRuntimeDriverResolution.Ready(new StorageRuntimeDriverLease(new RaisingDriver(ready.Lease, _fault)))
                : resolution;
        }
    }

    /// <summary>The real driver with a throw put in front of it. Disposal is forwarded to the lease it came out of, so the credential material behind it is released exactly as production releases it.</summary>
    private sealed class RaisingDriver : IArtifactStorageDriver
    {
        private readonly StorageRuntimeDriverLease _lease;
        private readonly RaisingDestination _fault;

        public RaisingDriver(StorageRuntimeDriverLease lease, RaisingDestination fault)
        {
            _lease = lease;
            _fault = fault;
        }

        public StorageProviderCapabilities Capabilities => _lease.Driver.Capabilities;

        public ValueTask<ArtifactStorageHeadResult> HeadAsync(ArtifactStorageHeadRequest request, CancellationToken cancellationToken)
        {
            if (!_fault.RaisesAbout(request.ObjectKey)) return _lease.Driver.HeadAsync(request, cancellationToken);

            _fault.Threw();

            throw new IOException($"The destination could not be asked about '{request.ObjectKey}'.");
        }

        public ValueTask<ArtifactStorageProbeResult> ProbeAsync(ArtifactStorageProbeRequest request, CancellationToken cancellationToken)
        {
            _fault.Probed();

            if (_fault.AnswersForItself) return _lease.Driver.ProbeAsync(request, cancellationToken);

            throw new IOException("The destination could not be asked whether it is still answering.");
        }

        public ValueTask<ArtifactStoragePutResult> PutAsync(ArtifactStoragePutRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A verification pass writes nothing at a destination.");

        public ValueTask<ArtifactStorageReadResult> OpenReadAsync(ArtifactStorageReadRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A verification pass reads no object bodies.");

        public ValueTask<ArtifactStorageDeleteResult> DeleteAsync(ArtifactStorageDeleteRequest request, CancellationToken cancellationToken) => throw new NotSupportedException("A verification pass deletes nothing.");

        public ValueTask DisposeAsync() => _lease.DisposeAsync();
    }

    /// <summary>Deletes the destination's root as the pass opens the Nth row that lives there, which is a mount going away between two rows of one batch.</summary>
    private sealed class VanishingBroker : IStorageRuntimeDriverBroker
    {
        private readonly IStorageRuntimeDriverBroker _inner;
        private readonly Destination _destination;
        private readonly int _afterRows;
        private int _rows;

        public VanishingBroker(IStorageRuntimeDriverBroker inner, Destination destination, int afterRows)
        {
            _inner = inner;
            _destination = destination;
            _afterRows = afterRows;
        }

        public bool Vanished { get; private set; }

        public ValueTask<StorageRuntimeDriverResolution> OpenAsync(StorageRuntimeDriverRequest request, CancellationToken cancellationToken)
        {
            if (request.TeamId == _destination.TeamId && ++_rows == _afterRows)
            {
                Directory.Delete(_destination.Root, recursive: true);
                Vanished = true;
            }

            return _inner.OpenAsync(request, cancellationToken);
        }
    }

    /// <summary>The first batch-ordering statement a pass issued, verbatim, so its plan can be pinned without a copy of the query living in this file.</summary>
    private sealed class CapturedOrdering : DbCommandInterceptor
    {
        public string? CommandText { get; private set; }

        public List<(string Name, object? Value)> Parameters { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);

            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Capture(command);

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command)
        {
            if (CommandText != null || !command.CommandText.Contains("ROW_NUMBER", StringComparison.OrdinalIgnoreCase)) return;

            CommandText = command.CommandText;

            foreach (DbParameter parameter in command.Parameters) Parameters.Add((parameter.ParameterName, parameter.Value));
        }
    }
}
