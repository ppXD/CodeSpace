using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Budget;
using CodeSpace.Core.Services.Workflows.Retention;
using CodeSpace.Core.Services.Workflows.Retention.Cursors;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Agents.Recovery;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Retention;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// The two record planes the retention reaper reclaims by DELETING rows rather than bytes: settled cleanup receipts
/// and terminal budget claims. Both are ledger rows whose whole content is a statement about something that is over,
/// and both are protected by the same two waits as every other class — an age floor from the record's own terminal
/// instant, then a quarantine from the first observation that nothing cites it.
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

    // ── Cleanup receipts ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The positive control. Two sweeps: the first records the quarantine deadline and removes nothing, the second collects.</summary>
    [Theory]
    [InlineData(RunResourceOutcome.Completed)]
    [InlineData(RunResourceOutcome.Compensated)]
    [InlineData(RunResourceOutcome.Unknown)]
    public async Task A_settled_receipt_past_its_rule_is_reclaimed(RunResourceOutcome outcome)
    {
        var world = await SeedWorldAsync();
        var receipt = await ReceiptAsync(world, outcome, RunResourceKind.Spool);
        await AgeReceiptAsync(receipt, TimeSpan.FromDays(31));

        await SweepAsync();

        (await ReceiptExistsAsync(receipt)).ShouldBeTrue("the sweep that first noticed the receipt must not delete it");
        (await RetainUntilOfReceiptAsync(receipt)).ShouldNotBeNull("the quarantine deadline has to be durable — an in-memory one is no wait at all");

        await ElapseReceiptQuarantineAsync(receipt);
        await SweepAsync();

        (await ReceiptExistsAsync(receipt)).ShouldBeFalse("both waits elapsed with nothing citing it");
    }

    /// <summary>
    /// An orphan is addressed to a sweep on the host that owes the teardown and can still become Compensated.
    /// Mutation: admit Orphaned into the claim and this reds — with a leaked resource nobody will ever be told about.
    /// </summary>
    [Fact]
    public async Task An_orphaned_receipt_is_never_claimed_however_old()
    {
        var world = await SeedWorldAsync();
        var orphan = await ReceiptAsync(world, RunResourceOutcome.Orphaned, RunResourceKind.EgressSubnet, ownerHost: "some-dead-host");
        await AgeReceiptAsync(orphan, TimeSpan.FromDays(400));

        (await ClaimedReceiptsAsync()).ShouldNotContain(orphan);

        await SweepAsync();
        await SweepAsync();

        (await ReceiptExistsAsync(orphan)).ShouldBeTrue("the row naming the host that still owes this teardown is the only record of it");
        (await RetainUntilOfReceiptAsync(orphan)).ShouldBeNull("and it is never even quarantined, because no sweep may propose collecting it");
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

        (await ClaimedReceiptsAsync()).ShouldNotContain(receipt, "a cited row must not even occupy a batch slot");
        await SweepAsync();

        (await ReceiptExistsAsync(receipt)).ShouldBeTrue("a sealed result still cites this receipt; no elapsed window outranks that");
        (await CursorFor(DurableRecordClass.CleanupReceipt).ClassifyAsync(CandidateFor(world, receipt), CancellationToken.None))
            .ShouldBe(DurableReferenceVerdict.Referenced, "and the verdict says WHY it was kept, not merely that it was");
    }

    /// <summary>The age floor at the CALL SITE: the window the loop computes from the class's rule is what keeps a young receipt out of the batch.</summary>
    [Fact]
    public async Task A_receipt_inside_its_age_floor_is_never_claimed()
    {
        var world = await SeedWorldAsync();
        var young = await ReceiptAsync(world, RunResourceOutcome.Completed, RunResourceKind.Cgroup);
        await AgeReceiptAsync(young, DurableRetentionPolicy.CleanupReceipt.MinimumAge - TimeSpan.FromDays(1));

        (await ClaimedReceiptsAsync()).ShouldNotContain(young);

        await AgeReceiptAsync(young, TimeSpan.FromDays(2));

        (await ClaimedReceiptsAsync()).ShouldContain(young, "the counter-example is worth nothing unless the same row IS claimed once its floor has passed");
    }

    // ── Budget claims ──────────────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(BudgetReservationStates.Settled)]
    [InlineData(BudgetReservationStates.Released)]
    [InlineData(BudgetReservationStates.Expired)]
    [InlineData(BudgetReservationStates.Reconciled)]
    public async Task A_terminal_budget_claim_past_its_rule_is_reclaimed(string state)
    {
        var world = await SeedWorldAsync();
        var claim = await ReservationAsync(world, state);
        await AgeReservationAsync(claim, TimeSpan.FromDays(91));

        await SweepAsync();

        (await ReservationExistsAsync(claim)).ShouldBeTrue("the sweep that first noticed the claim must not delete it");
        (await RetainUntilOfReservationAsync(claim)).ShouldNotBeNull();

        await ElapseReservationQuarantineAsync(claim);
        await SweepAsync();

        (await ReservationExistsAsync(claim)).ShouldBeFalse("both waits elapsed with nothing citing it");
    }

    /// <summary>
    /// A live claim is spend a team cap is still counting. Mutation: claim on "not terminal" rather than on the
    /// allow-list, or add a live state to it, and this reds — with a team handed back money it has not finished
    /// spending.
    /// </summary>
    [Theory]
    [InlineData(BudgetReservationStates.Reserved)]
    [InlineData(BudgetReservationStates.InFlight)]
    [InlineData(BudgetReservationStates.Indeterminate)]
    public async Task A_live_budget_claim_is_never_claimed_however_old(string state)
    {
        var world = await SeedWorldAsync();
        var live = await ReservationAsync(world, state);
        await AgeReservationAsync(live, TimeSpan.FromDays(400));

        (await ClaimedReservationsAsync()).ShouldNotContain(live);

        await SweepAsync();
        await SweepAsync();

        (await ReservationExistsAsync(live)).ShouldBeTrue($"'{state}' is holding cap; the ledger's admission arithmetic still counts it");
        (await RetainUntilOfReservationAsync(live)).ShouldBeNull();
    }

    /// <summary>
    /// The physical receipt that spent under this claim still names it. Mutation: drop the attempt probe from the
    /// claim and the classification, and the sweep proposes a delete the database then refuses as a constraint
    /// violation — one row's keep turned into a sweep-wide error.
    /// </summary>
    [Fact]
    public async Task A_budget_claim_a_model_call_receipt_names_survives()
    {
        var world = await SeedWorldAsync();
        var workflowRunId = await WorkflowRunAsync(world);
        var claim = await ReservationAsync(world with { WorkflowRunId = workflowRunId }, BudgetReservationStates.Settled);
        await NameFromModelCallAttemptAsync(world, claim, workflowRunId);
        await AgeReservationAsync(claim, TimeSpan.FromDays(400));

        (await ClaimedReservationsAsync()).ShouldNotContain(claim, "a cited row must not even occupy a batch slot");
        await SweepAsync();

        (await ReservationExistsAsync(claim)).ShouldBeTrue();
        (await CursorFor(DurableRecordClass.BudgetReservation).ClassifyAsync(CandidateFor(world, claim), CancellationToken.None))
            .ShouldBe(DurableReferenceVerdict.Referenced);
    }

    /// <summary>A parent claim a child is still accounted under. Mutation: drop the child probe and a wave's claim goes while the agent claims beneath it still point at it.</summary>
    [Fact]
    public async Task A_budget_claim_a_child_is_accounted_under_survives()
    {
        var world = await SeedWorldAsync();
        var parent = await ReservationAsync(world, BudgetReservationStates.Settled);
        await ReservationAsync(world, BudgetReservationStates.Settled, parent: parent);
        await AgeReservationAsync(parent, TimeSpan.FromDays(400));

        (await ClaimedReservationsAsync()).ShouldNotContain(parent);
        await SweepAsync();

        (await ReservationExistsAsync(parent)).ShouldBeTrue();
        (await CursorFor(DurableRecordClass.BudgetReservation).ClassifyAsync(CandidateFor(world, parent), CancellationToken.None))
            .ShouldBe(DurableReferenceVerdict.Referenced);
    }

    [Fact]
    public async Task A_budget_claim_inside_its_age_floor_is_never_claimed()
    {
        var world = await SeedWorldAsync();
        var young = await ReservationAsync(world, BudgetReservationStates.Settled);
        await AgeReservationAsync(young, DurableRetentionPolicy.BudgetReservation.MinimumAge - TimeSpan.FromDays(1));

        (await ClaimedReservationsAsync()).ShouldNotContain(young);

        await AgeReservationAsync(young, TimeSpan.FromDays(2));

        (await ClaimedReservationsAsync()).ShouldContain(young, "the counter-example is worth nothing unless the same row IS claimed once its floor has passed");
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

    private async Task<Guid> ReservationAsync(World world, string state, Guid? parent = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.BudgetReservation.Add(new BudgetReservation
        {
            Id = id, TeamId = world.TeamId, WorkflowRunId = world.WorkflowRunId, ParentReservationId = parent,
            Kind = BudgetKinds.AgentAttempt, ScopeKey = $"scope/{id:N}", State = state, ReservedUsd = 1m,
            SettledUsd = state == BudgetReservationStates.Settled ? 1m : null, PriceVersion = "test/v1",
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        });
        await db.SaveChangesAsync();

        return id;
    }

    /// <summary>
    /// A physical model-call receipt that spent under this claim — the citer migration 0200 also enforces with
    /// ON DELETE RESTRICT. The call row needs a REAL workflow run behind it, so this is the one staging path that
    /// stands one up rather than using a free identifier: <c>budget_reservation.workflow_run_id</c> carries no foreign
    /// key of its own, and every other test here is about the claim rather than what spent under it.
    /// </summary>
    private async Task<Guid> NameFromModelCallAttemptAsync(World world, Guid reservation, Guid workflowRunId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var callId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.WorkflowRunModelCall.Add(new WorkflowRunModelCall
        {
            Id = callId, TeamId = world.TeamId, WorkflowRunId = workflowRunId, CallOrdinal = 1,
            Purpose = "retention-test/v1", CaptureSource = "structured-post/v1",
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        });
        db.WorkflowRunModelCallAttempt.Add(new WorkflowRunModelCallAttempt
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = workflowRunId, ModelCallId = callId,
            AttemptOrdinal = 1, Status = "Succeeded", CandidateId = Guid.NewGuid(), CandidateOrdinal = 1,
            CandidateModel = "test-model", BudgetReservationId = reservation, PricingSnapshotJson = "{}",
            CaptureSource = "structured-post/v1",
            CreatedDate = now, CreatedBy = world.ActorId, LastModifiedDate = now, LastModifiedBy = world.ActorId,
        });
        await db.SaveChangesAsync();

        return callId;
    }

    /// <summary>A real workflow run for the one citer that needs one, through the same seed every budget test uses.</summary>
    private async Task<Guid> WorkflowRunAsync(World world)
    {
        using var scope = _fixture.BeginScopeAs(world.ActorId, world.TeamId, Messages.Constants.Roles.Admin);
        var workflowId = await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "retention-" + Guid.NewGuid().ToString("N")[..8], Description = null,
            Definition = Infrastructure.WorkflowsTestSeed.MinimalDefinition(),
            Activations = [], Enabled = true,
        });

        return await Infrastructure.WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, world.TeamId);
    }

    /// <summary>A pin of the shape a sealed qualification result writes, without standing up a whole campaign to get one.</summary>
    private async Task PinAsync(World world, DurablePinKind kind, Guid target)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var groupId = Guid.NewGuid();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO paired_qualification_result_pin (id, result_id, kind, pinned_id, pinned_artifact_id, pinned_at) VALUES ({0}, {1}, {2}, {3}, NULL, now())",
            [Guid.NewGuid(), await SealedResultAsync(world, groupId), kind.ToString(), target]);
    }

    // ── Time travel ────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task AgeReceiptAsync(Guid receipt, TimeSpan age) => await ShiftAsync(
        "UPDATE agent_run_cleanup_receipt SET recorded_at = recorded_at - {0}::interval WHERE id = {1}", age, receipt);

    private async Task AgeReservationAsync(Guid reservation, TimeSpan age) => await ShiftAsync(
        "UPDATE budget_reservation SET created_date = created_date - {0}::interval, last_modified_date = last_modified_date - {0}::interval WHERE id = {1}", age, reservation);

    private async Task ElapseReceiptQuarantineAsync(Guid receipt) => await ShiftAsync(
        "UPDATE agent_run_cleanup_receipt SET retain_until = now() - interval '1 second' WHERE id = {1}", TimeSpan.Zero, receipt);

    private async Task ElapseReservationQuarantineAsync(Guid reservation) => await ShiftAsync(
        "UPDATE budget_reservation SET retain_until = now() - interval '1 second' WHERE id = {1}", TimeSpan.Zero, reservation);

    private async Task ShiftAsync(string sql, TimeSpan age, Guid id)
    {
        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(sql, [$"{age.TotalSeconds} seconds", id])).ShouldBe(1);
    }

    // ── Reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task SweepAsync()
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IDurableRetentionReaper>().SweepAsync(CancellationToken.None);
    }

    private IDurableRetentionCursor CursorFor(DurableRecordClass value)
    {
        using var scope = _fixture.BeginScope();

        return scope.Resolve<IEnumerable<IDurableRetentionCursor>>().Single(cursor => cursor.Class == value);
    }

    /// <summary>What the production claim query returns for the window the loop would build right now.</summary>
    private async Task<IReadOnlyList<Guid>> ClaimedAsync(DurableRecordClass value)
    {
        var rule = DurableRetentionPolicy.For(value).ShouldNotBeNull();
        var now = DateTimeOffset.UtcNow;
        var claimed = await CursorFor(value).ClaimAsync(new DurableRetentionSweepWindow(now, now - rule.MinimumAge, now - rule.RecheckInterval), 200, CancellationToken.None);

        return claimed.Select(candidate => candidate.Id).ToList();
    }

    private Task<IReadOnlyList<Guid>> ClaimedReceiptsAsync() => ClaimedAsync(DurableRecordClass.CleanupReceipt);
    private Task<IReadOnlyList<Guid>> ClaimedReservationsAsync() => ClaimedAsync(DurableRecordClass.BudgetReservation);

    private static DurableRetentionCandidate CandidateFor(World world, Guid id) => new(id, world.TeamId, 0, DateTimeOffset.UtcNow.AddDays(-400), null);

    private async Task<bool> ReceiptExistsAsync(Guid receipt)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunCleanupReceipt.AsNoTracking().AnyAsync(row => row.Id == receipt);
    }

    private async Task<DateTimeOffset?> RetainUntilOfReceiptAsync(Guid receipt)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().AgentRunCleanupReceipt.AsNoTracking().Where(row => row.Id == receipt).Select(row => row.RetainUntil).SingleAsync();
    }

    private async Task<bool> ReservationExistsAsync(Guid reservation)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().AnyAsync(row => row.Id == reservation);
    }

    private async Task<DateTimeOffset?> RetainUntilOfReservationAsync(Guid reservation)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().BudgetReservation.AsNoTracking().Where(row => row.Id == reservation).Select(row => row.RetainUntil).SingleAsync();
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

        return new World(teamId, actorId, runId, Guid.NewGuid());
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
    /// Removes what this class staged and the sweeps did not. The rows carry no bytes and no artifact placements, so
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
                var db = scope.Resolve<CodeSpaceDbContext>();
                await db.Database.ExecuteSqlRawAsync("DELETE FROM workflow_run_model_call_attempt WHERE team_id = {0}", [team]);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM workflow_run_model_call WHERE team_id = {0}", [team]);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM budget_reservation WHERE team_id = {0} AND parent_reservation_id IS NOT NULL", [team]);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM budget_reservation WHERE team_id = {0}", [team]);
                await db.Database.ExecuteSqlRawAsync("DELETE FROM agent_run_cleanup_receipt WHERE team_id = {0}", [team]);
            }
            catch { /* best-effort: a row a sweep already reclaimed has nothing left to give back */ }
        }
    }

    private sealed record World(Guid TeamId, Guid ActorId, Guid AgentRunId, Guid WorkflowRunId);
}
