using System.Text.RegularExpressions;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Persistence;

/// <summary>
/// Real-Postgres proof that a declaration nobody ever met stops being a shortfall against a number and becomes the
/// honest "nobody established what belongs here".
///
/// <para>The producer contract is what creates the hole: an expectation is declared BEFORE the records land and
/// accounted for after, so a worker killed in between leaves a terminal run whose facet is short against a count no
/// process will ever meet — and whose gap plane says nothing, because the producer that would have noticed the loss is
/// the process that died. Every neighbouring plane already had a sweep for exactly that; this one did not.</para>
///
/// <para>The assertion that matters most is the one about what did NOT change. <c>present_record_count</c> is read back
/// on every path here, because closing the shortfall by advancing it would leave the run reading Exact over records
/// nobody counted — the one false claim the completeness plane exists to refuse, reached by the sweep meant to protect
/// it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RunDataManifestReconcilerFlowTests
{
    private const int Batch = 100;

    /// <summary>
    /// How many bounded passes the race test will spend reaching the one row it seeded. A pass takes <see cref="Batch"/>
    /// candidates from the WHOLE database this collection shares, ordered by a team id this test does not choose, so a
    /// backlog the neighbouring classes left can push this row out of any single pass. Every row a pass un-states leaves
    /// the candidate set for good, so the backlog drains and the row is reached — this only bounds the wait.
    /// </summary>
    private const int ExaminationPasses = 20;

    private readonly PostgresFixture _fixture;

    public RunDataManifestReconcilerFlowTests(PostgresFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The shape the sweep exists for: three records declared, one accounted for, no gap naming the other two, and a
    /// run that will never advance anything again. It resolves INDETERMINATE — not to a complete record, and not to a
    /// shortfall against a number that has stopped meaning anything.
    /// </summary>
    [Fact]
    public async Task A_terminal_runs_unattributed_shortfall_resolves_indeterminate()
    {
        var world = await SeedTerminalRunAsync();
        var statement = await SeedShortfallAsync(world);

        var before = await StatementAsync(statement.Id);
        before.ExpectedRecordCount.ShouldBe(3, "precondition: the expectation must still be DETERMINATE, or this test would be watching a facet that was already indeterminate before the sweep ran");
        before.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial);

        await ReconcileAsync();

        var after = await StatementAsync(statement.Id);
        after.Verdict.IsStrictlyReadable().ShouldBeFalse(
            $"the sweep UN-STATES. Reading '{after.Verdict}' over {after.PresentRecordCount} of {after.ExpectedRecordCount?.ToString() ?? "an unstated number of"} records means the shortfall was closed by COUNTING — a complete record claimed over data nobody counted, which is the one thing this plane exists to refuse");
        after.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
        after.ExpectedRecordCount.ShouldBeNull("an expectation no producer will ever meet is not a number to measure against — it is an expectation nobody established");
        after.PresentRecordCount.ShouldBe(1, "no record was captured by the sweep, so no record may be counted by it");
        after.Revision.ShouldBe(statement.Revision + 1);
    }

    /// <summary>
    /// The same thing reached the way production reaches it: through the real producer seam. The declaration commits,
    /// the worker dies before the payload write, and the run terminalizes with the accounting that would have met the
    /// declaration never made. One tick of the reconciler later, the facet reads indeterminate.
    /// </summary>
    [Fact]
    public async Task A_worker_killed_between_the_declaration_and_the_payload_write_is_indeterminate_one_tick_later()
    {
        var world = await SeedTerminalRunAsync();

        await DeclareAsync(world, expected: 3);

        var declared = await StatementAsync(world, WorkflowRunDataOwnerKinds.NativeRecord);
        declared.ExpectedRecordCount.ShouldBe(3, "precondition: the declaration must have committed, or the payload write is not what went missing");
        declared.PresentRecordCount.ShouldBe(0);

        await ReconcileAsync();

        var reconciled = await StatementAsync(world, WorkflowRunDataOwnerKinds.NativeRecord);
        reconciled.ExpectedRecordCount.ShouldBeNull();
        reconciled.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
        reconciled.PresentRecordCount.ShouldBe(0, "nothing was captured, and a sweep that said otherwise would be inventing the records the dead worker never wrote");
    }

    /// <summary>
    /// An ATTRIBUTED shortfall is left exactly as it was. The gap plane can already say what is missing and why, and
    /// "unknown" is a strictly worse answer than a located loss — the sweep exists to replace a stale number with an
    /// honest one, never to replace evidence with a shrug.
    /// </summary>
    [Fact]
    public async Task A_shortfall_the_gap_plane_can_already_name_keeps_its_attribution()
    {
        var world = await SeedTerminalRunAsync();
        var statement = await SeedShortfallAsync(world);
        await SeedOpenGapAsync(world);

        var attributed = await StatementAsync(statement.Id);
        attributed.KnownMissingCount.ShouldBe(1, "precondition: the gap must have reached the statement, or this test proves nothing about attributed shortfalls");

        await ReconcileAsync();

        var after = await StatementAsync(statement.Id);
        after.ExpectedRecordCount.ShouldBe(3, "a shortfall with an open gap already says WHAT is missing; un-stating it would trade that for an unknown");
        after.KnownMissingCount.ShouldBe(1);
        after.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
        after.PresentRecordCount.ShouldBe(1, "the sweep captured nothing, so it may count nothing — advancing present here would close an ATTRIBUTED shortfall by inventing the very records the open gap says are missing");
        after.Revision.ShouldBe(attributed.Revision, "a statement the sweep is not allowed to touch must come back at the revision the gap's downgrade left it on, or something wrote a statement nobody can point at");
    }

    /// <summary>
    /// A run still going is short of its declaration for the ordinary reason: its producers have not accounted for what
    /// they undertook yet. Un-statable only once nothing will advance it again.
    /// </summary>
    [Fact]
    public async Task A_run_that_is_still_going_is_left_to_finish_its_own_accounting()
    {
        var world = await SeedRunAsync();
        var statement = await SeedShortfallAsync(world);

        await ReconcileAsync();

        var after = await StatementAsync(statement.Id);
        after.ExpectedRecordCount.ShouldBe(3, "a running run's producers have not finished accounting; the shortfall is the normal mid-flight shape and un-stating it destroys a record that was about to be established");
        after.PresentRecordCount.ShouldBe(1, "no record was captured by the sweep, so no record may be counted by it");
        after.Revision.ShouldBe(statement.Revision);
    }

    /// <summary>
    /// THE RACE, driven rather than hoped for. The sweep reads the candidate in one transaction and writes in another,
    /// and the whole window between them belongs to somebody else: the producer everyone assumed was dead commits the
    /// accounting that meets its declaration, or a gap lands and names the loss exactly. Either answer is strictly
    /// better than the "unknown" the sweep was on its way to write, and both are unrecoverable once written over —
    /// un-stating latches in <c>expectation_declared</c> and every later delta is absorbed.
    ///
    /// <para>The interleaving is committed from a second scope in the gap between the read and the write, so this is
    /// the real ordering rather than a lucky one. What must hold is that the write re-asks the question the read
    /// asked: a row that stopped qualifying is left exactly as the interleaved writer left it.</para>
    ///
    /// <para><b>Every assertion here reads the one row this test seeded, and nothing reads the pass's tally.</b> This is
    /// a DEPLOYMENT-WIDE sweep over the database this whole collection shares, so <c>Examined</c> counts whatever
    /// unattributed shortfalls the neighbouring classes left behind, and a bounded batch can exclude this row from a
    /// pass entirely — a tally assertion fails on a stranger's leftover and passes on a coincidence, in both directions.
    /// The arithmetic those counts are made of is held deterministically in the unit tier instead, over a batch the test
    /// itself chooses and a writer that refuses a known number of it:
    /// <c>RunDataManifestReconcileTests.Every_candidate_a_pass_picks_up_is_counted_in_exactly_one_arm</c>. What
    /// stands in for them here is stronger: the interleaving fires only for THIS run, so its firing is the proof the
    /// sweep actually reached this row rather than skipping it, and the row's own <c>revision</c> is the proof nothing
    /// wrote to it afterwards.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_answer_that_lands_between_the_probe_and_the_write_is_not_written_over(bool accounted)
    {
        var world = await SeedTerminalRunAsync();
        var statement = await SeedShortfallAsync(world);

        var interleaved = await ReconcileUntilExaminedAsync(world, () => accounted ? AdvanceDeliverableAsync(world, present: 2) : SeedOpenGapAsync(world));

        var after = await StatementAsync(statement.Id);
        after.ExpectedRecordCount.ShouldBe(3,
            $"the expectation was un-stated over an answer that landed first. The row stopped being an unattributed shortfall before the write ran ({(accounted ? "its producer accounted for the missing records" : "a gap named the loss")}), and un-stating it destroyed that permanently — the write has to re-check the condition that selected it, not trust a read from an earlier transaction");
        after.PresentRecordCount.ShouldBe(accounted ? 3 : 1);
        after.KnownMissingCount.ShouldBe(accounted ? 0 : 1);
        after.Verdict.ShouldBe(accounted ? WorkflowRunCaptureCompleteness.Exact : WorkflowRunCaptureCompleteness.Partial);
        after.Revision.ShouldBe(interleaved.Revision,
            "the row moved after the interleaved answer committed, and the sweep is the only thing that touched it — every write on this plane bumps the revision, so a statement the write was supposed to leave alone came back on a revision the interleaved writer did not leave it on");
    }

    /// <summary>
    /// The PLAN, because nothing about losing it would be visible in a result. Every fifteen minutes this sweep asks
    /// the deployment-wide question "which statement is short of a declaration nobody met", and the only thing that
    /// keeps that off a sequential scan of every facet of every run is that its predicate carries
    /// <c>ix_workflow_run_data_manifest_incomplete</c>'s own condition — which PostgreSQL then proves the query implies.
    ///
    /// <para>Asked deterministically rather than by cost: on a suite-sized table every plan is cheap and the winner
    /// would be noise, so the competing manifest indexes are dropped inside a transaction that is rolled back and
    /// sequential scans are penalised, leaving exactly one question — can the planner reach the partial index from this
    /// predicate AT ALL. The second half is the mutation, run in the same breath: strip the verdict conjunct and the
    /// same query cannot reach it under the same conditions, which is what makes the first half a finding about the
    /// predicate rather than a coincidence of this table's statistics.</para>
    /// </summary>
    [Fact]
    public async Task The_deployment_wide_sweep_reaches_the_partial_index_over_incomplete_statements()
    {
        var settledBefore = DateTimeOffset.UtcNow;
        string sql;
        using (var scope = _fixture.BeginScope())
            sql = RunDataManifestReconciler.AbandonedQuery(scope.Resolve<CodeSpaceDbContext>(), settledBefore, Batch).ToQueryString();

        await using var connection = new NpgsqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var isolated = await connection.BeginTransactionAsync();

        await ExecuteAsync(connection, isolated, "SET LOCAL enable_seqscan = off");
        await ExecuteAsync(connection, isolated, "DROP INDEX ux_workflow_run_data_manifest_facet");
        await ExecuteAsync(connection, isolated, "DROP INDEX ix_workflow_run_data_manifest_run");

        var plan = await ExplainAsync(connection, isolated, Body(sql), settledBefore);
        var withoutVerdict = await ExplainAsync(connection, isolated, WithoutVerdictConjunct(Body(sql)), settledBefore);

        await isolated.RollbackAsync();

        plan.ShouldContain("ix_workflow_run_data_manifest_incomplete",
            customMessage: $"the quarter-hourly deployment-wide sweep cannot reach the partial index over incomplete statements, so every tick reads every facet of every run. Plan was:\n{plan}");
        withoutVerdict.ShouldNotContain("ix_workflow_run_data_manifest_incomplete",
            customMessage: $"the same query reaches the index WITHOUT the verdict conjunct, so the assertion above is measuring a coincidence rather than the predicate — and the conjunct the reconciler carries for the plan's sake is buying nothing. Plan without it was:\n{withoutVerdict}");
    }

    /// <summary>
    /// Passes until the sweep reaches the facet THIS test seeded, and hands back that row as the interleaved answer left
    /// it — the baseline every "the sweep did not write over it" assertion is made against.
    ///
    /// <para>The loop and the run-id scope are both what a bounded, deployment-wide sweep over a shared database costs.
    /// Unscoped, the interleaving fires once per candidate the sweep found ANYWHERE and applies this run's late answer
    /// that many times, so a single stranger's leftover silently doubles it. Unlooped, a backlog can push this row out
    /// of the one pass, nothing is raced, and the assertions hold vacuously.</para>
    /// </summary>
    private async Task<WorkflowRunDataManifest> ReconcileUntilExaminedAsync(RunWorld world, Func<Task> betweenReadAndWrite)
    {
        WorkflowRunDataManifest? interleaved = null;

        async Task InterleaveOnceForThisRun(RunDataAbandonedExpectation candidate)
        {
            if (candidate.WorkflowRunId != world.RunId) return;

            await betweenReadAndWrite();

            interleaved = await StatementAsync(world, WorkflowRunDataOwnerKinds.Deliverable);
        }

        for (var pass = 0; pass < ExaminationPasses && interleaved == null; pass++)
            await ReconcileAsync(InterleaveOnceForThisRun);

        if (interleaved == null)
            throw new InvalidOperationException($"{ExaminationPasses} passes of {Batch} never picked up this run's Deliverable facet, so nothing was raced and every assertion after this point would hold for the wrong reason. Each pass permanently drains what it un-states, so a backlog this deep means rows are qualifying for the sweep's read and being refused by its write — count them with: SELECT count(*) FROM workflow_run_data_manifest WHERE expected_record_count IS NOT NULL AND present_record_count < expected_record_count AND known_missing_count = 0");

        return interleaved;
    }

    /// <summary>One pass on the real production class, with the clock moved past the settling window so a statement written moments ago reads as one nothing has advanced for a quarter of an hour.</summary>
    private async Task<RunDataManifestReconciliation> ReconcileAsync(Func<RunDataAbandonedExpectation, Task>? betweenReadAndWrite = null)
    {
        using var scope = _fixture.BeginScope(builder =>
        {
            builder.RegisterInstance<TimeProvider>(new SkewedClock(RunDataManifestReconciler.SettlingWindow + TimeSpan.FromMinutes(1))).As<TimeProvider>().SingleInstance();

            if (betweenReadAndWrite != null)
                builder.Register(context => new InterleavingWriter(context.Resolve<RunDataCompletenessWriter>(), betweenReadAndWrite)).As<IRunDataAbandonedExpectationWriter>().InstancePerLifetimeScope();
        });

        return await scope.Resolve<IRunDataManifestReconciler>().ReconcileUnattributedShortfallsAsync(Batch, CancellationToken.None);
    }

    /// <summary>The producer half of the interleaving: a contained accounting for the records the sweep had already decided nobody would ever account for.</summary>
    private async Task AdvanceDeliverableAsync(RunWorld world, long present)
    {
        using var scope = _fixture.BeginScope();

        var advanced = await scope.Resolve<IRunDataCompletenessWriter>().AdvanceAsync(new RunDataFacetAdvance
        {
            TeamId = world.TeamId, WorkflowRunId = world.RunId, Facet = WorkflowRunDataOwnerKinds.Deliverable, Expected = 0, Present = present,
        }, CancellationToken.None);

        advanced.ShouldBeTrue("the late accounting must commit, or the sweep is not racing anything");
    }

    /// <summary>Runs the interleaved writer's work on its own scope BEFORE delegating, so it commits strictly between the sweep's selecting read and the un-stating it feeds. Every candidate the sweep picked up is offered by name, because on a shared database most of them belong to somebody else and only the caller knows which row is its own.</summary>
    private sealed class InterleavingWriter(IRunDataAbandonedExpectationWriter inner, Func<RunDataAbandonedExpectation, Task> betweenReadAndWrite) : IRunDataAbandonedExpectationWriter
    {
        public async Task<bool> UnstateAbandonedExpectationAsync(RunDataAbandonedExpectation abandoned, CancellationToken cancellationToken)
        {
            await betweenReadAndWrite(abandoned);

            return await inner.UnstateAbandonedExpectationAsync(abandoned, cancellationToken);
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>EXPLAINs the SQL EF itself emits, with every parameter it declared bound — the timestamp cutoff by name, everything else the batch bound.</summary>
    private static async Task<string> ExplainAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, DateTimeOffset settledBefore)
    {
        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + sql, connection, transaction);

        foreach (var name in ParameterNames(sql))
            command.Parameters.AddWithValue(name, name.Contains("settled", StringComparison.OrdinalIgnoreCase) ? settledBefore : Batch);

        var lines = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));

        return string.Join('\n', lines);
    }

    /// <summary>Every distinct parameter EF named in the statement. A cutoff whose C# name stops containing "settled" binds as an integer and PostgreSQL says so loudly, which is the intended failure.</summary>
    private static IReadOnlyList<string> ParameterNames(string sql) => Regex.Matches(sql, @"@(\w+)")
        .Select(match => match.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    /// <summary>ToQueryString prefixes the statement with its parameter values as line comments; only the statement itself can be EXPLAINed.</summary>
    private static string Body(string sql) => string.Join('\n', sql.Split('\n').Where(line => !line.StartsWith("-- @", StringComparison.Ordinal)));

    /// <summary>The mutation: the same query with the one conjunct removed that the partial index's predicate is proved from.</summary>
    private static string WithoutVerdictConjunct(string sql) => sql
        .Replace(" AND w.verdict <> 'Exact' AND w.verdict <> 'RedactedExact'", string.Empty, StringComparison.Ordinal);

    /// <summary>The declaration half of a producer's advance, through the seam production uses: an expectation stated before any record of it is durable.</summary>
    private async Task DeclareAsync(RunWorld world, long expected)
    {
        using var scope = _fixture.BeginScope();

        var declared = await scope.Resolve<IRunDataCompletenessWriter>().AdvanceAsync(new RunDataFacetAdvance
        {
            TeamId = world.TeamId, WorkflowRunId = world.RunId, Facet = WorkflowRunDataOwnerKinds.NativeRecord, Expected = expected, Present = 0,
        }, CancellationToken.None);

        declared.ShouldBeTrue("the declaration must commit, or the run under test never had an expectation to fall short of");
    }

    private async Task<WorkflowRunDataManifest> StatementAsync(Guid statementId)
    {
        using var scope = _fixture.BeginScope();

        return await Manifests(scope).SingleAsync(statement => statement.Id == statementId);
    }

    private async Task<WorkflowRunDataManifest> StatementAsync(RunWorld world, string facet)
    {
        using var scope = _fixture.BeginScope();

        return await Manifests(scope).SingleAsync(statement => statement.WorkflowRunId == world.RunId && statement.Facet == facet);
    }

    private static IQueryable<WorkflowRunDataManifest> Manifests(ILifetimeScope scope) => scope.Resolve<CodeSpaceDbContext>().WorkflowRunDataManifest.AsNoTracking();

    /// <summary>Three records declared, one accounted for, nothing known-missing — the unattributed shortfall, seeded directly so the run's own producers are not part of what is under test.</summary>
    private async Task<WorkflowRunDataManifest> SeedShortfallAsync(RunWorld world)
    {
        var stamped = DateTimeOffset.UtcNow;
        var statement = new WorkflowRunDataManifest
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = world.RunId, Facet = WorkflowRunDataOwnerKinds.Deliverable,
            ExpectedRecordCount = 3, PresentRecordCount = 1, KnownMissingCount = 0,
            Verdict = WorkflowRunCaptureCompleteness.Partial, Revision = 1,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = stamped, LastModifiedAt = stamped,
        };

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunDataManifest.Add(statement);
        await db.SaveChangesAsync();

        return statement;
    }

    private async Task SeedOpenGapAsync(RunWorld world)
    {
        var noticed = DateTimeOffset.UtcNow;

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.WorkflowRunCaptureGap.Add(new WorkflowRunCaptureGap
        {
            Id = Guid.NewGuid(), TeamId = world.TeamId, WorkflowRunId = world.RunId,
            SubjectKind = WorkflowRunDataOwnerKinds.Deliverable, SubjectId = "reports/summary.md",
            Channel = NativeRecordChannel.SessionState, RangeKind = CaptureGapRangeKind.Unbounded,
            Reason = CaptureGapReason.WriteRefused, ReasonDetail = "the destination refused the deliverable's bytes",
            CaptureSource = "artifact-manifest-store", NoticedAt = noticed, Resolution = CaptureGapResolution.Open,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = noticed,
        });
        await db.SaveChangesAsync();
    }

    private async Task<RunWorld> SeedTerminalRunAsync()
    {
        var world = await SeedRunAsync();

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(candidate => candidate.Id == world.RunId);
        run.Status = WorkflowRunStatus.Success;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        return world;
    }

    private async Task<RunWorld> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CreateWorkflowCommand
            {
                Name = "manifest-reconciler-" + Guid.NewGuid().ToString("N")[..8],
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        return new RunWorld(await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId), teamId);
    }

    private sealed class SkewedClock(TimeSpan skew) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + skew;
    }

    private sealed record RunWorld(Guid RunId, Guid TeamId);
}
