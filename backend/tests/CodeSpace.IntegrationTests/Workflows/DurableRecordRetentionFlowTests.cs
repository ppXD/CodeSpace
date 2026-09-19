using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions.Room;
using CodeSpace.Core.Services.Workflows.Retention;
using CodeSpace.Core.Services.Workflows.Retention.Cursors;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents.Recovery;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Retention;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The one record plane the retention reaper reclaims by DELETING rows rather than bytes: settled cleanup receipts. A
/// receipt is a ledger row whose whole content is a statement about something that is over, and it is protected by the
/// same two waits as every other class — an age floor from the record's own terminal instant, then a quarantine from
/// the first observation that nothing cites it.
///
/// <para>Every assertion is about a row this class created. A sweep is deployment-wide over a shared database, so its
/// tallies count other classes' rows as readily as these; the counter-examples assert that THEIR row survived, and the
/// positive controls that THEIRS is gone.</para>
///
/// <para>The rows staged here carry no bytes and no artifact placements, so this class leaves nothing behind for the
/// bounded sweeps elsewhere in the suite to trip over — what it does not reclaim, it deletes in teardown.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DurableRecordRetentionFlowTests : IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private readonly List<Guid> _teams = [];

    public DurableRecordRetentionFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>The positive control. Two sweeps: the first records the quarantine deadline and removes nothing, the second collects.</summary>
    [Theory]
    [InlineData(RunResourceOutcome.Completed)]
    [InlineData(RunResourceOutcome.Compensated)]
    public async Task A_settled_receipt_past_its_rule_is_reclaimed(RunResourceOutcome outcome)
    {
        var world = await SeedWorldAsync();
        var receipt = await ReceiptAsync(world, outcome, RunResourceKind.Spool);
        await AgeReceiptAsync(receipt, TimeSpan.FromDays(31));

        await SweepAsync();

        (await ReceiptExistsAsync(receipt)).ShouldBeTrue("the sweep that first noticed the receipt must not delete it");
        (await RetainUntilAsync(receipt)).ShouldNotBeNull("the quarantine deadline has to be durable — an in-memory one is no wait at all");

        await ElapseQuarantineAsync(receipt);
        await SweepAsync();

        (await ReceiptExistsAsync(receipt)).ShouldBeFalse("both waits elapsed with nothing citing it");
    }

    /// <summary>
    /// An orphan is addressed to a sweep on the host that owes the teardown and can still become Compensated.
    /// Mutation: admit Orphaned into the claim and the allow-list, and this reds — with a leaked resource nobody will
    /// ever be told about.
    /// </summary>
    [Fact]
    public async Task An_orphaned_receipt_is_never_claimed_however_old()
    {
        var world = await SeedWorldAsync();
        var orphan = await ReceiptAsync(world, RunResourceOutcome.Orphaned, RunResourceKind.EgressSubnet, ownerHost: "some-dead-host");
        await AgeReceiptAsync(orphan, TimeSpan.FromDays(400));

        (await ClaimedAsync()).ShouldNotContain(orphan);

        await SweepAsync();
        await SweepAsync();

        (await ReceiptExistsAsync(orphan)).ShouldBeTrue("the row naming the host that still owes this teardown is the only record of it");
        (await RetainUntilAsync(orphan)).ShouldBeNull("and it is never even quarantined, because no sweep may propose collecting it");
    }

    /// <summary>
    /// An Unknown receipt is neither settled nor beyond reach: the ledger's upsert fences only Completed and
    /// Compensated, so a live orphan the owning host cannot even attempt to tear down moves Orphaned → Unknown and
    /// leaves the orphan queue for good. Mutation: put Unknown back in the reclaimable set and this reds — with a leak
    /// that had left the one queue that watches it now deleted by policy as well.
    /// </summary>
    [Fact]
    public async Task An_unknown_receipt_is_never_claimed_however_old()
    {
        var world = await SeedWorldAsync();
        var unknown = await ReceiptAsync(world, RunResourceOutcome.Unknown, RunResourceKind.Cgroup);
        await AgeReceiptAsync(unknown, TimeSpan.FromDays(400));

        (await ClaimedAsync()).ShouldNotContain(unknown);

        await SweepAsync();
        await SweepAsync();

        (await ReceiptExistsAsync(unknown)).ShouldBeTrue("nothing has settled this resource; the row is what says so");
        (await RetainUntilAsync(unknown)).ShouldBeNull();
    }

    /// <summary>
    /// The Room surface, which is why the sweepable outcomes are exactly the two the ledger calls settled. The
    /// recovery card counts Unknown receipts of the kinds a sweep CAN reach, over every receipt of the run and with no
    /// age bound at all — so a plane that drained them would walk that count down over days and then remove the card,
    /// putting a wrong number where the truth used to be. This asserts the card reads identically before and after a
    /// sweep old enough to have reclaimed everything it may.
    /// </summary>
    [Fact]
    public async Task The_rooms_recovery_card_reads_identically_before_and_after_a_sweep()
    {
        var world = await SeedWorldAsync();
        await ReceiptAsync(world, RunResourceOutcome.Unknown, RunResourceKind.Cgroup);
        await ReceiptAsync(world, RunResourceOutcome.Unknown, RunResourceKind.Workspace);
        await ReceiptAsync(world, RunResourceOutcome.Orphaned, RunResourceKind.EgressSubnet, ownerHost: "dead-host");
        var settled = await ReceiptAsync(world, RunResourceOutcome.Completed, RunResourceKind.Spool);
        foreach (var receipt in await ReceiptsOfAsync(world)) await AgeReceiptAsync(receipt, TimeSpan.FromDays(400));

        var before = RoomProjector.SummarizeRecovery(await RunCleanupReceiptsAsync(world)).ShouldNotBeNull();
        before.UnknownCount.ShouldBe(2, "the premise: the card is counting rows this sweep would otherwise be free to take");

        await SweepAsync();
        // Every receipt's deadline, not only the one that may go: a test that elapsed the quarantine of the settled
        // row alone would pass with Unknown rows merely waiting rather than protected.
        foreach (var receipt in await ReceiptsOfAsync(world)) await ElapseQuarantineAsync(receipt);
        await SweepAsync();

        (await ReceiptExistsAsync(settled)).ShouldBeFalse("the settled receipt IS reclaimed — otherwise this test passes by reclaiming nothing at all");
        var after = RoomProjector.SummarizeRecovery(await RunCleanupReceiptsAsync(world)).ShouldNotBeNull();
        after.UnknownCount.ShouldBe(before.UnknownCount, "an operator's unresolved-cleanup count must not fall because a retention pass ran");
        after.OrphanedCount.ShouldBe(before.OrphanedCount);
        after.Detail.ShouldBe(before.Detail, "and the sentence the card renders is the same sentence");
    }

    /// <summary>
    /// A receipt a sealed qualification result cites is evidence the result was computed from. Mutation: drop the pin
    /// predicate from the claim and the probe, and this reds with the evidence gone.
    /// </summary>
    [Fact]
    public async Task A_pinned_receipt_past_its_rule_survives()
    {
        var world = await SeedWorldAsync();
        var receipt = await ReceiptAsync(world, RunResourceOutcome.Completed, RunResourceKind.Workspace);
        await PinAsync(world, DurablePinKind.CleanupReceipt, receipt);
        await AgeReceiptAsync(receipt, TimeSpan.FromDays(400));

        (await ClaimedAsync()).ShouldNotContain(receipt, "a cited row must not even occupy a batch slot");
        await SweepAsync();

        (await ReceiptExistsAsync(receipt)).ShouldBeTrue("a sealed result still cites this receipt; no elapsed window outranks that");
        (await Cursor().ClassifyAsync(CandidateFor(world, receipt), CancellationToken.None))
            .ShouldBe(DurableReferenceVerdict.Referenced, "and the verdict says WHY it was kept, not merely that it was");
    }

    /// <summary>The age floor at the CALL SITE: the window the loop computes from the class's rule is what keeps a young receipt out of the batch.</summary>
    [Fact]
    public async Task A_receipt_inside_its_age_floor_is_never_claimed()
    {
        var world = await SeedWorldAsync();
        var young = await ReceiptAsync(world, RunResourceOutcome.Completed, RunResourceKind.Cgroup);
        await AgeReceiptAsync(young, DurableRetentionPolicy.CleanupReceipt.MinimumAge - TimeSpan.FromDays(1));

        (await ClaimedAsync()).ShouldNotContain(young);

        await AgeReceiptAsync(young, TimeSpan.FromDays(2));

        (await ClaimedAsync()).ShouldContain(young, "the counter-example is worth nothing unless the same row IS claimed once its floor has passed");
    }

    /// <summary>
    /// The deleting statement repeats every predicate that admitted the row, because the claim and the delete are
    /// different transactions. Mutation: drop the time predicates from <c>CollectAsync</c>'s Where and a decision
    /// taken about the row a receipt USED to be deletes the row it has become.
    /// </summary>
    [Theory]
    [InlineData("outcome = 'Orphaned', owner_host = 'a-host-that-came-back'")]
    [InlineData("recorded_at = now()")]
    [InlineData("retain_until = now() + interval '30 days'")]
    public async Task A_receipt_that_changed_after_the_claim_is_not_deleted(string moved)
    {
        var world = await SeedWorldAsync();
        var receipt = await ReceiptAsync(world, RunResourceOutcome.Completed, RunResourceKind.Spool);
        await AgeReceiptAsync(receipt, TimeSpan.FromDays(31));
        await SweepAsync();
        await ElapseQuarantineAsync(receipt);

        // The claim, taken while the row still reads collectable, and then the row moves under it.
        var candidate = (await CandidatesAsync()).Single(row => row.Id == receipt);
        await MoveAsync(receipt, moved);

        var settled = await Cursor().SettleAsync(Window(), candidate, DurableRetentionDecision.Collect(DateTimeOffset.UtcNow.AddDays(-1)), CancellationToken.None);

        settled.ShouldBeFalse("the decision was taken about a row that no longer exists in that shape");
        (await ReceiptExistsAsync(receipt)).ShouldBeTrue();
    }

    /// <summary>
    /// A receipt whose citation question could not be answered has nowhere else to record that it was looked at, so
    /// the sweep pushes its own deadline forward. Mutation: settle only Quarantine and Collect, and an unanswerable
    /// row is re-claimed on every tick for ever while the sweep reports a healthy claim count.
    /// </summary>
    [Fact]
    public async Task A_receipt_the_sweep_kept_for_an_unanswerable_reason_is_deferred()
    {
        var world = await SeedWorldAsync();
        var receipt = await ReceiptAsync(world, RunResourceOutcome.Completed, RunResourceKind.McpSocket);
        await AgeReceiptAsync(receipt, TimeSpan.FromDays(31));
        var candidate = (await CandidatesAsync()).Single(row => row.Id == receipt);

        var settled = await Cursor().SettleAsync(Window(), candidate, DurableRetentionDecision.Indeterminate("probe-unreachable", null), CancellationToken.None);

        settled.ShouldBeTrue();
        (await RetainUntilAsync(receipt)).ShouldNotBeNull().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddHours(23),
            "the next look is a day away, not the next tick");
        (await ClaimedAsync()).ShouldNotContain(receipt);
    }

    /// <summary>
    /// The claim is fair per tenant, so one team's backlog can never fill a batch; the loop reaches the rest by asking
    /// again. Mutation: claim once instead of looping and only the oldest receipt of the team is ever swept.
    /// </summary>
    [Fact]
    public async Task One_sweep_reaches_every_eligible_receipt_of_a_team_not_just_its_oldest()
    {
        var world = await SeedWorldAsync();
        var receipts = new List<Guid>();

        foreach (var kind in new[] { RunResourceKind.Spool, RunResourceKind.Workspace, RunResourceKind.Cgroup })
        {
            var receipt = await ReceiptAsync(world, RunResourceOutcome.Completed, kind);
            await AgeReceiptAsync(receipt, TimeSpan.FromDays(31));
            receipts.Add(receipt);
        }

        await SweepAsync();

        foreach (var receipt in receipts)
            (await RetainUntilAsync(receipt)).ShouldNotBeNull($"receipt {receipt} of the same team was never reached by the sweep");
    }

    /// <summary>
    /// The batch budget is per cursor, not shared. Mutation: spend one budget across them in class order and the
    /// plane behind a saturated one is never swept at all — while the sweep reports a healthy claim count, which is
    /// what makes it invisible.
    /// </summary>
    [Fact]
    public async Task A_saturated_cursor_does_not_starve_the_one_behind_it()
    {
        using var scope = _fixture.BeginScope();
        var saturating = new StubCursor(DurableRecordClass.LogStream, candidatesPerClaim: 25, claims: 20);
        var behind = new StubCursor(DurableRecordClass.CleanupReceipt, candidatesPerClaim: 1, claims: 1);
        var reaper = new DurableRetentionReaper(scope.Resolve<DbContextOptions<CodeSpaceDbContext>>(), [saturating, behind],
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DurableRetentionReaper>.Instance);

        var summary = await reaper.SweepAsync(CancellationToken.None);

        saturating.Settled.ShouldBe(200, "the first plane is offered more than one batch and must be held to exactly one");
        behind.Settled.ShouldBe(1, "and the plane behind it still gets its own batch, rather than the remainder of somebody else's");
        summary.Claimed.ShouldBe(201);
    }

    /// <summary>A cursor with no database behind it: it hands out as many candidates as it was told to and records what the loop did with them.</summary>
    private sealed class StubCursor(DurableRecordClass recordClass, int candidatesPerClaim, int claims) : IDurableRetentionCursor
    {
        private int _remainingClaims = claims;

        public DurableRecordClass Class => recordClass;
        public int Settled { get; private set; }

        public Task<IReadOnlyList<DurableRetentionCandidate>> ClaimAsync(DurableRetentionSweepWindow window, int limit, CancellationToken cancellationToken)
        {
            if (_remainingClaims-- <= 0) return Task.FromResult<IReadOnlyList<DurableRetentionCandidate>>([]);

            var terminal = window.TerminalBefore.AddDays(-1);

            return Task.FromResult<IReadOnlyList<DurableRetentionCandidate>>(
                Enumerable.Range(0, Math.Min(limit, candidatesPerClaim)).Select(_ => new DurableRetentionCandidate(Guid.NewGuid(), Guid.NewGuid(), 0, terminal, null)).ToList());
        }

        public Task<DurableReferenceVerdict> ClassifyAsync(DurableRetentionCandidate candidate, CancellationToken cancellationToken) =>
            Task.FromResult(DurableReferenceVerdict.Unreferenced);

        public Task<bool> SettleAsync(DurableRetentionSweepWindow window, DurableRetentionCandidate candidate, DurableRetentionDecision decision, CancellationToken cancellationToken)
        {
            Settled++;

            return Task.FromResult(true);
        }
    }

    // ── Staging ────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> ReceiptAsync(World world, RunResourceOutcome outcome, RunResourceKind kind, string? ownerHost = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        db.AgentRunCleanupReceipt.Add(new AgentRunCleanupReceiptRecord
        {
            Id = id, TeamId = world.TeamId, AgentRunId = world.AgentRunId, FenceEpoch = 7, Kind = kind, Outcome = outcome,
            OwnerHost = ownerHost, ResourceKey = $"{kind}/{id:N}", RecordedByHost = "retention-test", RecordedAt = DateTimeOffset.UtcNow,
            ErrorCode = outcome is RunResourceOutcome.Orphaned or RunResourceOutcome.Unknown ? "left-behind" : null,
        });
        await db.SaveChangesAsync();

        return id;
    }

    /// <summary>A pin of the shape a sealed qualification result writes, without standing up a whole campaign to get one.</summary>
    private async Task PinAsync(World world, DurablePinKind kind, Guid target)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO paired_qualification_result_pin (id, result_id, kind, pinned_id, pinned_artifact_id, pinned_at) VALUES ({0}, {1}, {2}, {3}, NULL, now())",
            [Guid.NewGuid(), await SealedResultAsync(world, Guid.NewGuid()), kind.ToString(), target]);
    }

    private async Task AgeReceiptAsync(Guid receipt, TimeSpan age) => await ExecuteAsync(
        "UPDATE agent_run_cleanup_receipt SET recorded_at = recorded_at - {0}::interval WHERE id = {1}", [$"{age.TotalSeconds} seconds", receipt]);

    private async Task ElapseQuarantineAsync(Guid receipt)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE agent_run_cleanup_receipt SET retain_until = now() - interval '1 second' WHERE id = {0}", [receipt]);
    }

    /// <summary>Moves the row under a claim already taken — the race the deleting statement's repeated predicates exist for.</summary>
    private async Task MoveAsync(Guid receipt, string change) => await ExecuteAsync(
        $"UPDATE agent_run_cleanup_receipt SET {change} WHERE id = {{0}}", [receipt]);

    private async Task ExecuteAsync(string sql, object[] parameters)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(sql, parameters)).ShouldBe(1);
    }

    // ── Reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task SweepAsync()
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IDurableRetentionReaper>().SweepAsync(CancellationToken.None);
    }

    private IDurableRetentionCursor Cursor()
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<IEnumerable<IDurableRetentionCursor>>().Single(cursor => cursor.Class == DurableRecordClass.CleanupReceipt);
    }

    /// <summary>The window the loop would build right now, from the class's own rule.</summary>
    private static DurableRetentionSweepWindow Window()
    {
        var rule = DurableRetentionPolicy.CleanupReceipt;
        var now = DateTimeOffset.UtcNow;

        return new DurableRetentionSweepWindow(now, now - rule.MinimumAge, now - rule.RecheckInterval, now + rule.RecheckInterval);
    }

    private async Task<IReadOnlyList<DurableRetentionCandidate>> CandidatesAsync() =>
        await Cursor().ClaimAsync(Window(), 200, CancellationToken.None);

    private async Task<IReadOnlyList<Guid>> ClaimedAsync() => (await CandidatesAsync()).Select(candidate => candidate.Id).ToList();

    private static DurableRetentionCandidate CandidateFor(World world, Guid id) => new(id, world.TeamId, 0, DateTimeOffset.UtcNow.AddDays(-400), null);

    private async Task<bool> ReceiptExistsAsync(Guid receipt)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunCleanupReceipt.AsNoTracking().AnyAsync(row => row.Id == receipt);
    }

    private async Task<DateTimeOffset?> RetainUntilAsync(Guid receipt)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunCleanupReceipt.AsNoTracking().Where(row => row.Id == receipt).Select(row => row.RetainUntil).SingleAsync();
    }

    private async Task<IReadOnlyList<Guid>> ReceiptsOfAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunCleanupReceipt.AsNoTracking()
            .Where(row => row.AgentRunId == world.AgentRunId).Select(row => row.Id).ToListAsync();
    }

    /// <summary>The run's receipts as the Room's own reader hands them to its fold.</summary>
    private async Task<IReadOnlyList<RunCleanupReceipt>> RunCleanupReceiptsAsync(World world)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpace.Core.Services.Agents.Recovery.IRunCleanupLedger>()
            .ForRunsAsync(world.TeamId, [world.AgentRunId], CancellationToken.None);
    }

    // ── The world ──────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<World> SeedWorldAsync()
    {
        var actorId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.User.Add(new User { Id = actorId, Email = $"record-retention-{actorId:N}@test.local", Name = "Record Retention" });
        db.Team.Add(new Team { Id = teamId, Slug = $"record-retention-{teamId:N}", Name = "Record Retention", Kind = TeamKind.Workspace });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = actorId, Role = TeamRole.Owner });
        await db.SaveChangesAsync();
        db.AgentRun.Add(new AgentRun
        {
            Id = runId, TeamId = teamId, Harness = "test-harness", Status = AgentRunStatus.Failed, TaskJson = "{}",
            FenceEpoch = 7, CreatedDate = now, CreatedBy = actorId, LastModifiedDate = now, LastModifiedBy = actorId,
        });
        await db.SaveChangesAsync();
        _teams.Add(teamId);

        return new World(teamId, actorId, runId);
    }

    /// <summary>A sealed result row for a pin to hang off — the pin's foreign key needs one, and a whole campaign is a different test's subject.</summary>
    private async Task<Guid> SealedResultAsync(World world, Guid groupId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(groupId.ToByteArray()));
        db.PairedQualificationProtocol.Add(new PairedQualificationProtocol
        {
            ObservationGroupId = groupId, TeamId = world.TeamId, SuiteDigest = "sha256:hidden", SuiteVersion = "sha256/retention:v1",
            CodeRevision = new string('c', 40), ControlModelRowId = Guid.NewGuid(), CandidateModelRowId = Guid.NewGuid(),
            StatisticsVersion = "paired-cluster-bootstrap/v2", Criterion = "Quality", SessionsPerCell = 1,
            MinimumIndependentClusters = 1, MinimumStrata = 1, MinimumRequiredExecutionClusters = 1, MinimumEvaluatorHealth = 1,
            MaxCostUsdPerLaunch = 3m, MinimumQualityLift = 0.05, OrderingSeed = "frozen-order", ProtocolDigest = digest,
        });
        await db.SaveChangesAsync();
        const string outcome = """{"pairedCells": 1, "qualifiedForCapabilityClaim": true}""";
        const string insert = "INSERT INTO paired_qualification_result (observation_group_id, protocol_digest, evidence_digest, result_digest, statistics_version, "
            + "expected_observation_count, observation_count, qualified_for_capability_claim, outcome_json, created_date, created_by, last_modified_date, last_modified_by) "
            + "VALUES ({0}, {1}, {2}, {3}, 'paired-cluster-bootstrap/v2', 2, 2, TRUE, {5}, now(), {4}, now(), {4})";
        await db.Database.ExecuteSqlRawAsync(insert, [groupId, digest, Digest(groupId, "evidence"), Digest(groupId, "result"), world.ActorId, outcome]);

        return groupId;
    }

    private static string Digest(Guid seed, string salt) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([.. seed.ToByteArray(), .. System.Text.Encoding.UTF8.GetBytes(salt)]));

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Removes what this class staged and the sweeps did not. These rows carry no bytes and no artifact placements, so
    /// nothing here can change what a bounded global sweep elsewhere in the suite sees — but a class that leaves
    /// eligible rows behind still hands the next sweep work it never meant to give it.
    /// </summary>
    public async Task DisposeAsync()
    {
        foreach (var team in _teams)
        {
            try
            {
                using var scope = _fixture.BeginScope();
                await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync("DELETE FROM agent_run_cleanup_receipt WHERE team_id = {0}", [team]);
            }
            catch { /* best-effort: a row a sweep already reclaimed has nothing left to give back */ }
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid AgentRunId);
}
