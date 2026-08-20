using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// The completeness plane's write path viewed from the SECOND producer's seat, against real Postgres (Rule 12 high
/// fidelity). Every test here calls the database exactly as a naive caller would — a bare connection, no transaction of
/// its own, no lock of its own — because that is the caller 0146 could not protect and 0148 has to.
///
/// <para><b>The trap being closed.</b> 0146's guards take the per-run rendezvous lock in a BEFORE ROW trigger, which
/// fires only after an INSERT's value expressions have already been evaluated on the statement snapshot. A producer
/// that probed the run's open gaps and then wrote therefore had its WHOLE statement refused whenever a gap committed in
/// between — and both counts are deltas, so a refused statement is not a retryable no-op: the delta is gone and the
/// run's expectation is understated for good. The first producer avoided that by taking the lock explicitly, which is a
/// rule a reader has to remember. 0148 makes the lock, the gap probe and the write one function whose first statement
/// is the lock, so the order is no longer choosable.</para>
///
/// <para><b>What only this tier can execute:</b> that the probe and the write see one set of gaps under contention,
/// which is a claim about an advisory lock, two triggers and a snapshot and about nothing in C#.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunDataCompletenessRendezvousTests
{
    private readonly PostgresFixture _fixture;

    public RunDataCompletenessRendezvousTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// THE headline, and the interleaving is FORCED rather than hoped for. A gap for this run is inserted and held
    /// uncommitted, so its own BEFORE ROW trigger is holding the run's rendezvous lock; a producer that holds no lock
    /// and opens no transaction then advances the statement, parks, and is released when the gap commits. That is
    /// exactly the window a probe outside the rendezvous falls into — the probe reads zero open gaps, the guard's
    /// re-probe under the lock reads one, the floor check refuses, and the whole statement is lost. A DELTA lost that
    /// way is not retryable, so the run's expectation is understated for good.
    ///
    /// <para>Written as a controlled hand-off on purpose: the same test with the two writers merely started together
    /// PASSES with the lock removed from <c>workflow_run_data_manifest_advance</c> — the window is microseconds wide and
    /// two independently-warming connections miss it — so a hopeful race here would have been a test that cannot fail
    /// while claiming to prove the lock is load-bearing.</para>
    /// </summary>
    [Fact]
    public async Task A_producer_that_takes_no_lock_of_its_own_never_loses_its_delta_to_a_gap_committing_underneath_it()
    {
        var run = await SeedRunAsync();

        using var holder = _fixture.BeginScope();
        var holding = holder.Resolve<CodeSpaceDbContext>();
        await using var uncommitted = await holding.Database.BeginTransactionAsync();

        holding.WorkflowRunCaptureGap.Add(OpenGap(run));
        await holding.SaveChangesAsync();

        var advance = AdvanceAsync(run, expected: 3, present: 3);

        (await ParkedOnTheRunLockAsync()).ShouldBeTrue(
            customMessage: "the advance never parked on the run's rendezvous lock, so the interleaving this test exists to force never happened and its verdict below means nothing. "
                         + "Diagnose with: psql -c \"SELECT l.granted, a.state, a.query FROM pg_locks l JOIN pg_stat_activity a USING (pid) WHERE l.locktype = 'advisory'\".");

        await uncommitted.CommitAsync();
        await advance;

        var statement = (await StatementOrNullAsync(run)).ShouldNotBeNull(
            customMessage: "the gap committing underneath the advance cost the producer its whole statement. That is the probe-then-write "
                         + "refusal: the counts it carried are a DELTA, so the advance is not a retryable no-op and the run's expectation "
                         + "is understated permanently. The lock has to be the FIRST statement of workflow_run_data_manifest_advance.");

        statement.ExpectedRecordCount.ShouldBe(3, customMessage: "the delta landed short, so part of it was lost to the interleaving");
        statement.PresentRecordCount.ShouldBe(3);
        statement.KnownMissingCount.ShouldBe(1, customMessage: "the gap was committed before the advance was released, so the statement it wrote must already count it");
        statement.Verdict.IsStrictlyReadable().ShouldBeFalse(
            customMessage: "the run read complete beside an open gap, so the probe and the guard saw different sets");

        (await OpenGapsAsync(run)).ShouldBe(1, customMessage: "the gap must be admitted whatever the statement claims — refusing the honest observation to protect the claim is the inversion this plane exists to prevent");
    }

    /// <summary>
    /// The reverse hand-off, which has to hold too and for a different reason: the statement is already complete when
    /// the gap arrives. Nothing in the advance can prevent that — the gap is written by someone else, later — so what
    /// carries it is 0146's own AFTER STATEMENT downgrade. The gap is admitted and the complete verdict comes down.
    /// </summary>
    [Fact]
    public async Task A_gap_arriving_after_a_complete_statement_downgrades_it_rather_than_being_refused()
    {
        var run = await SeedRunAsync();

        await AdvanceAsync(run, expected: 2, present: 2);
        (await StatementAsync(run)).Verdict.IsStrictlyReadable().ShouldBeTrue(customMessage: "the premise: the facet reads complete before the gap arrives");

        await NoticeGapAsync(run);

        var downgraded = await StatementAsync(run);
        downgraded.Verdict.IsStrictlyReadable().ShouldBeFalse(customMessage: "a span the run knows it missed un-completes the claim whichever order the two writers arrive in");
        downgraded.KnownMissingCount.ShouldBe(1);
        (await OpenGapsAsync(run)).ShouldBe(1);
    }

    /// <summary>
    /// The ORDER a producer advances in, and why the plane is allowed to declare an expectation it has not yet
    /// satisfied. 0146 assumes a producer that states what it expects BEFORE the records land, so that a death in
    /// between leaves present below expected and the facet fails closed. A single advance carrying both counts cannot
    /// do that: losing it understates them equally and the facet reads complete over frames nobody counted.
    ///
    /// <para>So the declaration has to be storable and has to read NOT complete on its own, and the presence that
    /// follows has to be able to raise it — including to the redacted arm, because a masked frame that landed is still
    /// a whole one.</para>
    /// </summary>
    [Theory]
    [InlineData(false, WorkflowRunCaptureCompleteness.Exact)]
    [InlineData(true, WorkflowRunCaptureCompleteness.RedactedExact)]
    public async Task An_expectation_declared_before_its_records_land_reads_not_complete_until_they_do(bool masked, WorkflowRunCaptureCompleteness landed)
    {
        var run = await SeedRunAsync();

        await AdvanceAsync(run, expected: 2, present: 0);

        var declared = await StatementAsync(run);
        declared.ExpectedRecordCount.ShouldBe(2, customMessage: "the expectation is declared ahead of the records, which is the only shape that leaves a shortfall visible when the accounting after them is lost");
        declared.PresentRecordCount.ShouldBe(0);
        declared.Verdict.IsStrictlyReadable().ShouldBeFalse(customMessage: "an undertaken-but-unlanded batch is a shortfall, and a shortfall is not complete");
        declared.Revision.ShouldBe(1);

        await AdvanceAsync(run, expected: 0, present: 2, masked: masked);

        var satisfied = await StatementAsync(run);
        satisfied.ExpectedRecordCount.ShouldBe(2, customMessage: "the presence advance adds nothing to the expectation the declaration already stated, or the batch would be counted twice");
        satisfied.PresentRecordCount.ShouldBe(2);
        satisfied.Verdict.ShouldBe(landed);
        satisfied.Verdict.IsStrictlyReadable().ShouldBeTrue();
        satisfied.Revision.ShouldBe(2);
    }

    /// <summary>
    /// An advance only ever ADDS. A delta that could go down is a complete verdict a writer can reach by subtraction —
    /// which is the one thing the counters must not permit, since nothing in the database compares them to the plane
    /// they describe. Recovering a gap lowers a count and is a different operation with its own citation requirement.
    /// </summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public async Task An_advance_that_could_subtract_is_refused(long expected, long present)
    {
        var run = await SeedRunAsync();

        await AdvanceAsync(run, expected: 4, present: 4);

        var refusal = await Should.ThrowAsync<PostgresException>(() => AdvanceAsync(run, expected, present));
        refusal.Message.ShouldContain("non-negative delta");

        var statement = await StatementAsync(run);
        statement.ExpectedRecordCount.ShouldBe(4, customMessage: "the refused advance may not have moved anything");
        statement.PresentRecordCount.ShouldBe(4);
    }

    /// <summary>
    /// Un-stating an expectation invents nothing and repeats nothing. A facet with no statement gets no row — an absent
    /// statement is already the indeterminate answer, and a row against a facet nobody produced would be a claim made
    /// up out of nothing. A facet already indeterminate is left alone rather than re-revised, so a terminalizer that
    /// runs twice does not walk the revision counter.
    /// </summary>
    [Fact]
    public async Task Un_stating_an_expectation_invents_no_statement_and_is_idempotent()
    {
        var run = await SeedRunAsync();

        (await UnstateAsync(run)).ShouldBe(0, customMessage: "there is no statement for this facet, and inventing one would be a claim nobody made");
        (await StatementOrNullAsync(run)).ShouldBeNull();

        await AdvanceAsync(run, expected: 5, present: 5);

        (await UnstateAsync(run)).ShouldBe(1);

        var indeterminate = await StatementAsync(run);
        indeterminate.ExpectedRecordCount.ShouldBeNull();
        indeterminate.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
        indeterminate.Revision.ShouldBe(2);

        (await UnstateAsync(run)).ShouldBe(0, customMessage: "already indeterminate — re-revising would advance the revision counter for a fact that did not change");
        (await StatementAsync(run)).Revision.ShouldBe(2);

        await AdvanceAsync(run, expected: 3, present: 3);

        var absorbed = await StatementAsync(run);
        absorbed.ExpectedRecordCount.ShouldBeNull(customMessage: "NULL + n is NULL: a later delta cannot restore a total nobody ever knew");
        absorbed.PresentRecordCount.ShouldBe(8);
        absorbed.Verdict.IsStrictlyReadable().ShouldBeFalse();
    }

    /// <summary>Exactly what a producer does: call the function on a bare scope, in no transaction, holding no lock.</summary>
    private async Task AdvanceAsync(SeededRun run, long expected, long present, bool masked = false)
    {
        using var scope = _fixture.BeginScope();

        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlAsync(
            $"SELECT workflow_run_data_manifest_advance({run.TeamId}, {run.WorkflowRunId}, {WorkflowRunDataOwnerKinds.NativeRecord}, {expected}, {present}, {masked}, {WorkflowRunDataContract.CurrentVersion})");
    }

    private async Task<long> UnstateAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return (await scope.Resolve<CodeSpaceDbContext>().Database.SqlQuery<long>(
                $"SELECT workflow_run_data_manifest_unstate_expectation({run.TeamId}, {run.WorkflowRunId}, {WorkflowRunDataOwnerKinds.NativeRecord}) AS \"Value\"")
            .ToListAsync()).Single();
    }

    /// <summary>A gap for the SAME facet, on its own connection, committed.</summary>
    private async Task NoticeGapAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunCaptureGap.Add(OpenGap(run));

        await db.SaveChangesAsync();
    }

    private static WorkflowRunCaptureGap OpenGap(SeededRun run)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = run.TeamId, WorkflowRunId = run.WorkflowRunId,
            SubjectKind = WorkflowRunDataOwnerKinds.NativeRecord, RangeKind = CaptureGapRangeKind.Unbounded,
            Reason = CaptureGapReason.BoundExceeded, ReasonDetail = "a span the racing producer knows it missed",
            CaptureSource = "test-harness/v1", NoticedAt = now, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = now,
        };
    }

    /// <summary>
    /// Waits until the advance is genuinely PARKED on the run's rendezvous lock. Reports rather than throws, so the
    /// caller decides what "they never overlapped" means — a helper that asserted it away would leave the test passing
    /// on an interleaving that never occurred.
    /// </summary>
    private async Task<bool> ParkedOnTheRunLockAsync()
    {
        const int pollMilliseconds = 25;
        const int attempts = 400;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (await WaitingRunLocksAsync() > 0) return true;

            await Task.Delay(pollMilliseconds);
        }

        return false;
    }

    private async Task<long> WaitingRunLocksAsync()
    {
        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_locks
            WHERE locktype = 'advisory' AND NOT granted
              AND database = (SELECT oid FROM pg_database WHERE datname = current_database())
            """, connection);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<WorkflowRunDataManifest> StatementAsync(SeededRun run) =>
        (await StatementOrNullAsync(run)).ShouldNotBeNull(customMessage: "the advance stated nothing at all, so there is no claim to assert about");

    private async Task<WorkflowRunDataManifest?> StatementOrNullAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId);
    }

    private async Task<int> OpenGapsAsync(SeededRun run)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRunCaptureGap
            .CountAsync(candidate => candidate.WorkflowRunId == run.WorkflowRunId && candidate.Resolution == CaptureGapResolution.Open);
    }

    private async Task<SeededRun> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;

        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "completeness-rendezvous-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return new SeededRun(teamId, await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId));
    }

    private sealed record SeededRun(Guid TeamId, Guid WorkflowRunId);
}
