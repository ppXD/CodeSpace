using Autofac;
using CodeSpace.Core.Services.RunData;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Pins the observation-only read side of the Workflow Run completeness manifest. It reports only statements that
/// producers actually made; an absent statement remains visibly unstated and is never folded into a run-wide green
/// verdict. No terminal or execution consumer participates in this flow.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class WorkflowRunDataCompletenessReadFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkflowRunDataCompletenessReadFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_run_with_no_statements_is_observably_unstated_not_exact()
    {
        var seeded = await SeedRunAsync();

        using var scope = _fixture.BeginScopeAs(seeded.UserId, seeded.TeamId, Roles.Admin);
        var view = await scope.Resolve<IMediator>().Send(new GetWorkflowRunDataCompletenessQuery { RunId = seeded.RunId });

        view.ShouldNotBeNull();
        view!.Scope.ShouldBe(WorkflowRunDataCompletenessScope.RecordedFacetsOnly);
        view.Facets.ShouldBeEmpty();
        view.HasStatements.ShouldBeFalse();
        view.RunWideVerdict.ShouldBeNull("facets with no producer statement are indeterminate, so this read model must not mint a run-wide verdict");
    }

    [Fact]
    public async Task Recorded_facets_return_materialized_counts_and_honest_independent_verdicts()
    {
        var seeded = await SeedRunAsync();
        using (var scope = _fixture.BeginScope())
        {
            var writer = scope.Resolve<IRunDataCompletenessWriter>();
            (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.NativeRecord, expected: 1, present: 1), CancellationToken.None)).ShouldBeTrue();
            (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.HarnessProcessAttempt, expected: 2, present: 1), CancellationToken.None)).ShouldBeTrue();
        }

        using var readScope = _fixture.BeginScope();
        var view = await readScope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None);

        view.ShouldNotBeNull();
        view!.Facets.Select(facet => facet.Facet).ShouldBe(new[] { WorkflowRunDataOwnerKinds.HarnessProcessAttempt, WorkflowRunDataOwnerKinds.NativeRecord });
        view.RunWideVerdict.ShouldBeNull("two recorded statements do not establish that every other facet was stated");

        var process = view.Facets[0];
        process.ExpectedRecordCount.ShouldBe(2);
        process.PresentRecordCount.ShouldBe(1);
        process.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
        process.IsStrictlyReadable.ShouldBeFalse();

        var native = view.Facets[1];
        native.ExpectedRecordCount.ShouldBe(1);
        native.PresentRecordCount.ShouldBe(1);
        native.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact);
        native.IsStrictlyReadable.ShouldBeTrue();
    }

    /// <summary>
    /// The run reached the engine's initialization and then terminalized before any producer stated anything — the
    /// bootstrap-failure shape. Every required facet has a row and none of them carries a producer's count, so the
    /// only honest run-wide answer is the indeterminate one; a complete verdict here would be a claim about records
    /// nobody ever counted.
    /// </summary>
    [Fact]
    public async Task A_run_that_terminalized_before_any_producer_stated_anything_is_not_reported_complete()
    {
        var seeded = await SeedRunAsync();
        using (var scope = _fixture.BeginScope())
        {
            (await scope.Resolve<IRunDataCompletenessWriter>().InitializeAsync(new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();
            await scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRun.Where(run => run.Id == seeded.RunId)
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));
        }

        using var readScope = _fixture.BeginScope();
        var view = (await readScope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();

        view.RunWideVerdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown,
            "initialization declares that a facet exists, not that it is empty; a run whose producers never counted anything may not read as a complete record");
        view.MissingFacetStatements.ShouldBeEmpty();
        view.Facets.Select(facet => facet.Facet).ShouldBe(RunDataManifestCoverage.RequiredFacets.Order(StringComparer.Ordinal).ToList());
        view.Facets.ShouldAllBe(facet => facet.ExpectedRecordCount == null);
        view.Facets.ShouldAllBe(facet => !facet.IsStrictlyReadable);
    }

    [Fact]
    public async Task Terminal_run_folds_only_after_every_registered_producer_facet_has_a_statement()
    {
        var seeded = await SeedRunAsync();
        using (var scope = _fixture.BeginScope())
        {
            var writer = scope.Resolve<IRunDataCompletenessWriter>();
            (await writer.InitializeAsync(new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();
            (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.ModelCall, expected: 1, present: 0), CancellationToken.None)).ShouldBeTrue();
            await scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRun.Where(run => run.Id == seeded.RunId)
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));
        }

        using var readScope = _fixture.BeginScope();
        var view = (await readScope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();

        view.IsTerminal.ShouldBeTrue();
        view.RequiredFacets.ShouldBe(RunDataManifestCoverage.RequiredFacets);
        view.MissingFacetStatements.ShouldBeEmpty();
        view.RunWideVerdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial, "one facet short of what it declared outranks three nobody has stated");
    }

    /// <summary>
    /// The other direction, and the one that proves the indeterminate initializer did not simply switch the plane off:
    /// every registered producer declares its expectation over the statement initialization minted and then accounts
    /// for it, and the run folds to Exact. The initializer's NULL is establishable; only an UN-STATED one absorbs.
    /// </summary>
    [Fact]
    public async Task Every_producer_stating_its_facet_over_an_initialized_run_still_folds_to_exact()
    {
        var seeded = await SeedRunAsync();
        using (var scope = _fixture.BeginScope())
        {
            var writer = scope.Resolve<IRunDataCompletenessWriter>();
            (await writer.InitializeAsync(new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();
            foreach (var facet in RunDataManifestCoverage.RequiredFacets)
            {
                (await writer.AdvanceAsync(Advance(seeded, facet, expected: 2, present: 0), CancellationToken.None)).ShouldBeTrue();
                (await writer.AdvanceAsync(Advance(seeded, facet, expected: 0, present: 2), CancellationToken.None)).ShouldBeTrue();
            }

            await scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRun.Where(run => run.Id == seeded.RunId)
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Success));
        }

        using var readScope = _fixture.BeginScope();
        var view = (await readScope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();

        view.Facets.ShouldAllBe(facet => facet.ExpectedRecordCount == 2 && facet.PresentRecordCount == 2);
        view.Facets.ShouldAllBe(facet => facet.IsStrictlyReadable);
        view.RunWideVerdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact,
            "an expectation nobody had declared yet is established by the first declaration, not absorbed by it");
    }

    /// <summary>
    /// Conditional producer coverage belongs to the RUN that undertook it, not to the deployment that happens to read
    /// it. The declaration is made while the run is live; once it exists, the terminal fold owes that facet in
    /// addition to the four always-applicable engine facets. A process-wide static list cannot express this without
    /// either invalidating every older run or pretending a run that never used the producer used it.
    /// </summary>
    [Fact]
    public async Task A_conditional_producer_declaration_becomes_required_only_for_the_run_that_made_it()
    {
        var producerRun = await SeedRunAsync();
        var siblingRun = await SeedRunAsync();

        using (var scope = _fixture.BeginScope())
        {
            var writer = scope.Resolve<IRunDataCompletenessWriter>();
            (await writer.InitializeAsync(new RunDataManifestInitialization(producerRun.TeamId, producerRun.RunId), CancellationToken.None)).ShouldBeTrue();
            (await writer.InitializeAsync(new RunDataManifestInitialization(siblingRun.TeamId, siblingRun.RunId), CancellationToken.None)).ShouldBeTrue();
            (await writer.AdvanceAsync(Advance(producerRun, WorkflowRunDataOwnerKinds.SemanticEvent, expected: 1, present: 0), CancellationToken.None)).ShouldBeTrue();

            await scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRun
                .Where(run => run.Id == producerRun.RunId || run.Id == siblingRun.RunId)
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));
        }

        using var readScope = _fixture.BeginScope();
        var reader = readScope.Resolve<IRunDataCompletenessReader>();
        var producer = (await reader.ReadAsync(producerRun.RunId, producerRun.TeamId, CancellationToken.None)).ShouldNotBeNull();
        var sibling = (await reader.ReadAsync(siblingRun.RunId, siblingRun.TeamId, CancellationToken.None)).ShouldNotBeNull();

        producer.RequiredFacets.ShouldContain(WorkflowRunDataOwnerKinds.SemanticEvent,
            customMessage: "the expected semantic event is part of this run's declared coverage, so omitting it from required facets would let the run-wide fold ignore a known shortfall");
        producer.RunWideVerdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
        sibling.RequiredFacets.ShouldNotContain(WorkflowRunDataOwnerKinds.SemanticEvent,
            customMessage: "a conditional facet on one run may not retroactively become an obligation of every run in the deployment");
    }

    /// <summary>
    /// Terminality seals WHICH facets the fold covers, while late accounting may still improve an already-declared
    /// one. Otherwise an asynchronous or buggy producer can make a terminal Exact verdict retroactively omit and then
    /// acquire a new obligation. A continued run reopens the set, because terminality itself is not monotonic here.
    /// </summary>
    [Fact]
    public async Task A_terminal_run_rejects_a_new_coverage_facet()
    {
        var seeded = await SeedRunAsync();

        using (var scope = _fixture.BeginScope())
        {
            var writer = scope.Resolve<IRunDataCompletenessWriter>();
            (await writer.InitializeAsync(new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();
            await scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRun.Where(run => run.Id == seeded.RunId)
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));

            (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.SemanticEvent, expected: 1, present: 0), CancellationToken.None))
                .ShouldBeFalse("the run's terminal coverage snapshot is sealed; a new facet may not appear behind a verdict already exposed to readers");
        }

        using var sealedScope = _fixture.BeginScope();
        var sealedView = (await sealedScope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        sealedView.RequiredFacets.ShouldNotContain(WorkflowRunDataOwnerKinds.SemanticEvent);
    }

    [Fact]
    public async Task A_continued_run_reopens_a_new_generation_that_can_declare_a_facet()
    {
        var seeded = await SeedRunAsync();

        using (var scope = _fixture.BeginScope())
        {
            (await scope.Resolve<IRunDataCompletenessWriter>().InitializeAsync(new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();
            await scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRun.Where(run => run.Id == seeded.RunId)
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));
        }

        using (var continuedScope = _fixture.BeginScope())
        {
            await continuedScope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRun.Where(run => run.Id == seeded.RunId)
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Running));

            var writer = continuedScope.Resolve<IRunDataCompletenessWriter>();
            (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.SemanticEvent, expected: 1, present: 0), CancellationToken.None)).ShouldBeTrue();
            await continuedScope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRun.Where(run => run.Id == seeded.RunId)
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));
        }

        using var finalScope = _fixture.BeginScope();
        var continued = (await finalScope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        continued.RequiredFacets.ShouldContain(WorkflowRunDataOwnerKinds.SemanticEvent);
        continued.RunWideVerdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
        var coverage = await finalScope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRunDataCoverage.AsNoTracking()
            .SingleAsync(candidate => candidate.TeamId == seeded.TeamId && candidate.WorkflowRunId == seeded.RunId);
        coverage.State.ShouldBe(CodeSpace.Core.Persistence.Entities.WorkflowRunDataCoverageStates.Sealed);
        coverage.Generation.ShouldBe(2, "one continue opens exactly one new applicability generation");
    }

    [Fact]
    public async Task A_sealed_existing_facet_can_finish_accounting_without_moving_the_snapshot()
    {
        var seeded = await SeedRunAsync();

        using (var scope = _fixture.BeginScope())
        {
            var writer = scope.Resolve<IRunDataCompletenessWriter>();
            (await writer.InitializeAsync(new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();
            (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.SemanticEvent, expected: 1, present: 0), CancellationToken.None)).ShouldBeTrue();
            await scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRun.Where(run => run.Id == seeded.RunId)
                .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));

            (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.SemanticEvent, expected: 0, present: 1), CancellationToken.None)).ShouldBeTrue();
        }

        using var readScope = _fixture.BeginScope();
        var view = (await readScope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        var semantic = view.Facets.Single(facet => facet.Facet == WorkflowRunDataOwnerKinds.SemanticEvent);
        semantic.ExpectedRecordCount.ShouldBe(1);
        semantic.PresentRecordCount.ShouldBe(1);
        semantic.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact);
        view.RequiredFacets.Count(facet => facet == WorkflowRunDataOwnerKinds.SemanticEvent).ShouldBe(1,
            "late accounting may improve an existing answer but may not append the applicability member again");
    }

    [Fact]
    public async Task Present_only_accounting_cannot_invent_a_conditional_facet()
    {
        var seeded = await SeedRunAsync();

        using var scope = _fixture.BeginScope();
        var writer = scope.Resolve<IRunDataCompletenessWriter>();
        (await writer.InitializeAsync(new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();
        (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.SemanticEvent, expected: 0, present: 1), CancellationToken.None))
            .ShouldBeFalse("a recovery observation does not prove that this producer applied to the run; only its positive undertaking does");

        var view = (await scope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        view.RequiredFacets.ShouldNotContain(WorkflowRunDataOwnerKinds.SemanticEvent);
        view.Facets.ShouldNotContain(facet => facet.Facet == WorkflowRunDataOwnerKinds.SemanticEvent);
    }

    [Fact]
    public async Task The_legacy_sql_entry_point_cannot_turn_zero_or_null_into_conditional_applicability_but_positive_declarations_still_work()
    {
        var zero = await SeedRunAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();

        var refusedZero = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT workflow_run_data_manifest_advance({zero.TeamId}, {zero.RunId}, {WorkflowRunDataOwnerKinds.SemanticEvent}, 0, 0, FALSE, {WorkflowRunDataContract.CurrentVersion})"));
        refusedZero.Message.ShouldContain("positive producer declaration");

        var refusedNull = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO workflow_run_data_manifest
                (id, team_id, workflow_run_id, facet, expected_record_count, present_record_count, known_missing_count,
                 verdict, masked_observed, expectation_declared, revision, schema_version, created_at, last_modified_at)
            VALUES ({{Guid.NewGuid()}}, {{zero.TeamId}}, {{zero.RunId}}, {{WorkflowRunDataOwnerKinds.SemanticEvent}}, NULL,
                    0, 0, 'LegacyUnknown', FALSE, FALSE, 1, 1, clock_timestamp(), clock_timestamp())
            """));
        refusedNull.Message.ShouldContain("positive producer declaration");

        (await db.WorkflowRunDataCoverage.AnyAsync(candidate => candidate.WorkflowRunId == zero.RunId)).ShouldBeFalse(
            "a refused first statement rolls its provisional header back rather than leaving a false takeover boundary");

        var positive = await SeedRunAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT workflow_run_data_manifest_advance({positive.TeamId}, {positive.RunId}, {WorkflowRunDataOwnerKinds.SemanticEvent}, 1, 0, FALSE, {WorkflowRunDataContract.CurrentVersion})");
        (await db.WorkflowRunDataCoverageFacet.AsNoTracking().AnyAsync(candidate => candidate.WorkflowRunId == positive.RunId
            && candidate.Facet == WorkflowRunDataOwnerKinds.SemanticEvent)).ShouldBeTrue(
            "rolling old callers retain their positive-declaration behavior through the database trigger");
    }

    [Fact]
    public async Task Initialization_materializes_the_persisted_baseline_even_after_a_later_deployment_retires_one_of_its_facets()
    {
        var seeded = await SeedRunAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        var oldBaseline = new[] { WorkflowRunDataOwnerKinds.ModelCall, WorkflowRunDataOwnerKinds.NativeRecord };
        var newBaseline = new[] { WorkflowRunDataOwnerKinds.ModelCall };

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT workflow_run_data_manifest_advance_covered({seeded.TeamId}, {seeded.RunId}, {WorkflowRunDataOwnerKinds.ModelCall}, 1, 0, FALSE, {oldBaseline}, 1)");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT workflow_run_data_manifest_initialize({seeded.TeamId}, {seeded.RunId}, {newBaseline}, 1)");

        var statements = await db.WorkflowRunDataManifest.AsNoTracking().Where(candidate => candidate.WorkflowRunId == seeded.RunId)
            .OrderBy(candidate => candidate.Facet).Select(candidate => candidate.Facet).ToListAsync();
        statements.ShouldContain(WorkflowRunDataOwnerKinds.NativeRecord,
            customMessage: "the header captured v1's question before initialization recovered; v2's smaller process list cannot erase that persisted question");
    }

    [Fact]
    public async Task A_baseline_facet_cannot_be_disguised_as_a_conditional_suffix()
    {
        var seeded = await SeedRunAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage (id, team_id, workflow_run_id, state, generation, revision, baseline_facets, schema_version, created_at, last_modified_at, sealed_at) VALUES ({Guid.NewGuid()}, {seeded.TeamId}, {seeded.RunId}, 'Open', 1, 1, {RunDataManifestCoverage.RequiredFacets.ToArray()}, 1, clock_timestamp(), clock_timestamp(), NULL)");

        var disguised = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage_facet (id, team_id, workflow_run_id, facet, ordinal, declared_generation, schema_version, created_at) VALUES ({Guid.NewGuid()}, {seeded.TeamId}, {seeded.RunId}, {WorkflowRunDataOwnerKinds.ModelCall}, 5, 1, 1, {DateTimeOffset.UtcNow})"));
        disguised.Message.ShouldContain("must match the frozen baseline facet and ordinal");
    }

    [Fact]
    public async Task A_legacy_run_keeps_the_same_frozen_question_and_verdict_when_its_first_writer_takes_it_over()
    {
        var seeded = await SeedRunAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
            foreach (var facet in RunDataManifestCoverage.LegacyV1Facets.Append(WorkflowRunDataOwnerKinds.SemanticEvent))
            {
                var conditional = facet == WorkflowRunDataOwnerKinds.SemanticEvent;
                var expected = conditional ? 2 : 1;
                var present = 1;
                var verdict = conditional ? "Partial" : "Exact";
                await db.Database.ExecuteSqlInterpolatedAsync($$"""
                    INSERT INTO workflow_run_data_manifest
                        (id, team_id, workflow_run_id, facet, expected_record_count, present_record_count,
                         known_missing_count, verdict, masked_observed, expectation_declared, revision,
                         schema_version, created_at, last_modified_at)
                    VALUES ({{Guid.NewGuid()}}, {{seeded.TeamId}}, {{seeded.RunId}}, {{facet}}, {{expected}}, {{present}}, 0, {{verdict}},
                            FALSE, TRUE, 1, 1, clock_timestamp(), clock_timestamp())
                    """);
            }
            await transaction.CommitAsync();
        }
        await db.WorkflowRun.Where(run => run.Id == seeded.RunId)
            .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));

        var reader = scope.Resolve<IRunDataCompletenessReader>();
        var before = (await reader.ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        before.RequiredFacets.ShouldBe(RunDataManifestCoverage.LegacyV1Facets.Append(WorkflowRunDataOwnerKinds.SemanticEvent).ToList());
        before.Facets.Where(facet => RunDataManifestCoverage.LegacyV1Facets.Contains(facet.Facet, StringComparer.Ordinal))
            .ShouldAllBe(facet => facet.Verdict == WorkflowRunCaptureCompleteness.Exact);
        before.Facets.Single(facet => facet.Facet == WorkflowRunDataOwnerKinds.SemanticEvent).Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial);
        before.RunWideVerdict.ShouldBe(WorkflowRunCaptureCompleteness.Partial,
            "a historical extra statement was already part of the old fold and cannot disappear because no coverage header was backfilled");
        (await db.WorkflowRunDataCoverage.AnyAsync(candidate => candidate.WorkflowRunId == seeded.RunId)).ShouldBeFalse();

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT workflow_run_data_manifest_advance_covered({seeded.TeamId}, {seeded.RunId}, {WorkflowRunDataOwnerKinds.ModelCall}, 0, 0, FALSE, {RunDataManifestCoverage.RequiredFacets.ToArray()}, 1)");

        var after = (await reader.ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        after.RequiredFacets.ShouldBe(before.RequiredFacets);
        after.RunWideVerdict.ShouldBe(before.RunWideVerdict);
        var coverage = await db.WorkflowRunDataCoverage.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == seeded.RunId);
        coverage.BaselineFacets.ShouldBe(RunDataManifestCoverage.LegacyV1Facets);
        (await db.WorkflowRunDataCoverageFacet.AsNoTracking().Where(candidate => candidate.WorkflowRunId == seeded.RunId)
            .OrderBy(candidate => candidate.Ordinal).Select(candidate => candidate.Facet).ToListAsync()).ShouldBe(before.RequiredFacets);

        var lateFacet = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT workflow_run_data_manifest_advance_covered({seeded.TeamId}, {seeded.RunId}, {WorkflowRunDataOwnerKinds.ToolCall}, 1, 0, FALSE, {RunDataManifestCoverage.RequiredFacets.ToArray()}, 1)"));
        lateFacet.Message.ShouldContain("coverage is sealed");
    }

    [Fact]
    public async Task A_running_legacy_run_adopts_old_statements_and_can_declare_a_later_conditional_facet()
    {
        var seeded = await SeedRunAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL session_replication_role = replica");
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO workflow_run_data_manifest
                    (id, team_id, workflow_run_id, facet, expected_record_count, present_record_count,
                     known_missing_count, verdict, masked_observed, expectation_declared, revision,
                     schema_version, created_at, last_modified_at)
                VALUES ({{Guid.NewGuid()}}, {{seeded.TeamId}}, {{seeded.RunId}}, {{WorkflowRunDataOwnerKinds.ModelCall}},
                        1, 1, 0, 'Exact', FALSE, TRUE, 1, 1, clock_timestamp(), clock_timestamp())
                """);
            await transaction.CommitAsync();
        }

        var writer = scope.Resolve<IRunDataCompletenessWriter>();
        (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.SemanticEvent, expected: 1, present: 1), CancellationToken.None)).ShouldBeTrue();

        var coverage = await db.WorkflowRunDataCoverage.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == seeded.RunId);
        coverage.State.ShouldBe(CodeSpace.Core.Persistence.Entities.WorkflowRunDataCoverageStates.Open);
        coverage.BaselineFacets.ShouldBe(RunDataManifestCoverage.LegacyV1Facets);
        var members = await db.WorkflowRunDataCoverageFacet.AsNoTracking().Where(candidate => candidate.WorkflowRunId == seeded.RunId)
            .OrderBy(candidate => candidate.Ordinal).Select(candidate => candidate.Facet).ToListAsync();
        members.ShouldBe(RunDataManifestCoverage.LegacyV1Facets.Append(WorkflowRunDataOwnerKinds.SemanticEvent).ToList());
        (await scope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull()
            .Facets.Single(facet => facet.Facet == WorkflowRunDataOwnerKinds.SemanticEvent).Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact);
    }

    [Fact]
    public async Task A_raw_header_insert_holds_the_run_rendezvous_until_the_row_exists_before_terminal_sealing()
    {
        var seeded = await SeedRunAsync();
        await AssertRawInsertRendezvousAsync(seeded, "workflow_run_data_coverage", "zz_coverage_header_pause", $$"""
            INSERT INTO workflow_run_data_coverage
                (id, team_id, workflow_run_id, state, generation, revision, baseline_facets, schema_version,
                 created_at, last_modified_at, sealed_at)
            VALUES ('{{Guid.NewGuid()}}', '{{seeded.TeamId}}', '{{seeded.RunId}}', 'Open', 1, 1,
                    ARRAY['model-call','harness-execution','harness-process-attempt','native-record']::varchar[],
                    1, clock_timestamp(), clock_timestamp(), NULL)
            """);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRunDataCoverage.AsNoTracking()
            .SingleAsync(candidate => candidate.WorkflowRunId == seeded.RunId)).State.ShouldBe(CodeSpace.Core.Persistence.Entities.WorkflowRunDataCoverageStates.Sealed);
    }

    [Fact]
    public async Task A_raw_facet_insert_holds_the_run_rendezvous_until_membership_exists_before_terminal_sealing()
    {
        var seeded = await SeedRunAsync();
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IRunDataCompletenessWriter>().InitializeAsync(
                new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();

        await AssertRawInsertRendezvousAsync(seeded, "workflow_run_data_coverage_facet", "zz_coverage_facet_pause", $$"""
            INSERT INTO workflow_run_data_coverage_facet
                (id, team_id, workflow_run_id, facet, ordinal, declared_generation, schema_version, created_at)
            VALUES ('{{Guid.NewGuid()}}', '{{seeded.TeamId}}', '{{seeded.RunId}}', 'semantic-event', 5, 1, 1, clock_timestamp())
            """);

        using var readScope = _fixture.BeginScope();
        var db = readScope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        (await db.WorkflowRunDataCoverageFacet.AsNoTracking().AnyAsync(candidate => candidate.WorkflowRunId == seeded.RunId
            && candidate.Facet == WorkflowRunDataOwnerKinds.SemanticEvent)).ShouldBeTrue();
        (await db.WorkflowRunDataCoverage.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == seeded.RunId))
            .State.ShouldBe(CodeSpace.Core.Persistence.Entities.WorkflowRunDataCoverageStates.Sealed);
    }

    [Fact]
    public async Task Coverage_header_state_cannot_be_forged_updated_in_place_or_deleted()
    {
        var seeded = await SeedRunAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        (await scope.Resolve<IRunDataCompletenessWriter>().InitializeAsync(new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();

        var identity = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_run_data_coverage SET schema_version = schema_version + 1, created_at = created_at + interval '1 second', baseline_facets = array_append(baseline_facets, 'semantic-event'::varchar), revision = revision + 1 WHERE team_id = {seeded.TeamId} AND workflow_run_id = {seeded.RunId}"));
        identity.Message.ShouldContain("stable identity, schema and creation time are immutable");

        var sameState = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_run_data_coverage SET generation = generation + 1, revision = revision + 1 WHERE team_id = {seeded.TeamId} AND workflow_run_id = {seeded.RunId}"));
        sameState.Message.ShouldContain("no same-state rewrite");

        var revision = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_run_data_coverage SET state = 'Sealed', sealed_at = clock_timestamp(), revision = revision + 2 WHERE team_id = {seeded.TeamId} AND workflow_run_id = {seeded.RunId}"));
        revision.Message.ShouldContain("revision must advance by exactly one");

        var prematureSeal = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_run_data_coverage SET state = 'Sealed', sealed_at = clock_timestamp(), revision = revision + 1 WHERE team_id = {seeded.TeamId} AND workflow_run_id = {seeded.RunId}"));
        prematureSeal.Message.ShouldContain("Open to Sealed requires a terminal run and preserves generation");

        var deleted = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM workflow_run_data_coverage WHERE team_id = {seeded.TeamId} AND workflow_run_id = {seeded.RunId}"));
        deleted.Message.ShouldContain("cannot be deleted");

        var stored = await db.WorkflowRunDataCoverage.AsNoTracking().SingleAsync(candidate => candidate.TeamId == seeded.TeamId && candidate.WorkflowRunId == seeded.RunId);
        stored.State.ShouldBe(CodeSpace.Core.Persistence.Entities.WorkflowRunDataCoverageStates.Open);
        stored.Generation.ShouldBe(1);
        stored.Revision.ShouldBe(1);

        var absent = await SeedRunAsync();
        var falseInitialState = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage (id, team_id, workflow_run_id, state, generation, revision, baseline_facets, schema_version, created_at, last_modified_at, sealed_at) VALUES ({Guid.NewGuid()}, {absent.TeamId}, {absent.RunId}, 'Sealed', 1, 1, {RunDataManifestCoverage.RequiredFacets.ToArray()}, 1, clock_timestamp(), clock_timestamp(), clock_timestamp())"));
        falseInitialState.Message.ShouldContain("initial state must agree with workflow run terminality");

        var falseInitialCounters = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage (id, team_id, workflow_run_id, state, generation, revision, baseline_facets, schema_version, created_at, last_modified_at, sealed_at) VALUES ({Guid.NewGuid()}, {absent.TeamId}, {absent.RunId}, 'Open', 2, 1, {RunDataManifestCoverage.RequiredFacets.ToArray()}, 1, clock_timestamp(), clock_timestamp(), NULL)"));
        falseInitialCounters.Message.ShouldContain("starts at generation 1 revision 1");

        await db.WorkflowRun.Where(run => run.Id == seeded.RunId)
            .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));

        var prematureReopen = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE workflow_run_data_coverage SET state = 'Open', sealed_at = NULL, generation = generation + 1, revision = revision + 1 WHERE team_id = {seeded.TeamId} AND workflow_run_id = {seeded.RunId}"));
        prematureReopen.Message.ShouldContain("Sealed to Open requires a nonterminal run and advances generation exactly once");

        var appended = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage_facet (id, team_id, workflow_run_id, facet, ordinal, declared_generation, schema_version, created_at) VALUES ({Guid.NewGuid()}, {seeded.TeamId}, {seeded.RunId}, {WorkflowRunDataOwnerKinds.SemanticEvent}, 5, 1, 1, {DateTimeOffset.UtcNow})"));
        appended.Message.ShouldContain("cannot append conditional applicability to a sealed run");
    }

    [Fact]
    public async Task A_terminal_runs_first_initialization_fills_only_its_frozen_baseline_capacity()
    {
        var seeded = await SeedRunAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        await db.WorkflowRun.Where(run => run.Id == seeded.RunId)
            .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT workflow_run_data_coverage_ensure({seeded.TeamId}, {seeded.RunId}, {RunDataManifestCoverage.RequiredFacets.ToArray()}, {WorkflowRunDataContract.CurrentVersion})");

        var appended = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage_facet (id, team_id, workflow_run_id, facet, ordinal, declared_generation, schema_version, created_at) VALUES ({Guid.NewGuid()}, {seeded.TeamId}, {seeded.RunId}, {WorkflowRunDataOwnerKinds.SemanticEvent}, 5, 1, 1, {DateTimeOffset.UtcNow})"));
        appended.Message.ShouldContain("cannot append conditional applicability to a sealed run",
            customMessage: "terminal bootstrap reserves exactly its captured generic baseline; an empty manifest table is not an initialization capability");

        (await scope.Resolve<IRunDataCompletenessWriter>().InitializeAsync(
            new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();
        var view = (await scope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        view.RequiredFacets.ShouldBe(RunDataManifestCoverage.RequiredFacets);
        view.Facets.Count.ShouldBe(RunDataManifestCoverage.RequiredFacets.Count,
            "first initialization on an already-terminal run must still materialize every frozen baseline statement");
    }

    [Fact]
    public async Task A_sealed_raw_header_cannot_disguise_a_conditional_facet_inside_a_reserved_ordinal()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        var direct = await SeedRunAsync();
        await db.WorkflowRun.Where(run => run.Id == direct.RunId)
            .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage (id, team_id, workflow_run_id, state, generation, revision, baseline_facets, schema_version, created_at, last_modified_at, sealed_at) VALUES ({Guid.NewGuid()}, {direct.TeamId}, {direct.RunId}, 'Sealed', 1, 1, {RunDataManifestCoverage.RequiredFacets.ToArray()}, 1, clock_timestamp(), clock_timestamp(), clock_timestamp())");
        var disguised = await Should.ThrowAsync<PostgresException>(db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage_facet (id, team_id, workflow_run_id, facet, ordinal, declared_generation, schema_version, created_at) VALUES ({Guid.NewGuid()}, {direct.TeamId}, {direct.RunId}, {WorkflowRunDataOwnerKinds.SemanticEvent}, 1, 1, 1, {DateTimeOffset.UtcNow})"));
        disguised.Message.ShouldContain("must match the frozen baseline facet and ordinal",
            customMessage: "a conditional facet cannot disguise itself inside an unfilled reserved ordinal of a raw terminal header");
    }

    [Fact]
    public async Task A_partial_raw_baseline_is_missing_not_exact_even_when_its_only_statement_is_exact()
    {
        var seeded = await SeedRunAsync();
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>();
        await db.WorkflowRun.Where(run => run.Id == seeded.RunId)
            .ExecuteUpdateAsync(update => update.SetProperty(run => run.Status, CodeSpace.Messages.Enums.WorkflowRunStatus.Failure));
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage (id, team_id, workflow_run_id, state, generation, revision, baseline_facets, schema_version, created_at, last_modified_at, sealed_at) VALUES ({Guid.NewGuid()}, {seeded.TeamId}, {seeded.RunId}, 'Sealed', 1, 1, {RunDataManifestCoverage.RequiredFacets.ToArray()}, 1, clock_timestamp(), clock_timestamp(), clock_timestamp())");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO workflow_run_data_coverage_facet (id, team_id, workflow_run_id, facet, ordinal, declared_generation, schema_version, created_at) VALUES ({Guid.NewGuid()}, {seeded.TeamId}, {seeded.RunId}, {WorkflowRunDataOwnerKinds.ModelCall}, 1, 1, 1, {DateTimeOffset.UtcNow})");
        (await scope.Resolve<IRunDataCompletenessWriter>().AdvanceAsync(
            Advance(seeded, WorkflowRunDataOwnerKinds.ModelCall, expected: 1, present: 1), CancellationToken.None)).ShouldBeTrue();

        var view = (await scope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        view.RequiredFacets.ShouldBe(RunDataManifestCoverage.RequiredFacets,
            customMessage: "the immutable header baseline is authority even when its member materialization is partial");
        view.MissingFacetStatements.ShouldBe(RunDataManifestCoverage.RequiredFacets.Skip(1).ToList());
        view.Facets.Single().Verdict.ShouldBe(WorkflowRunCaptureCompleteness.Exact,
            "precondition: the one materialized statement alone would fold Exact under the old member-derived reader");
        view.RunWideVerdict.ShouldBeNull("a partial baseline cannot be rounded up to the verdict of the rows that happened to land");
    }

    [Fact]
    public async Task A_terminal_transition_waiting_on_the_rendezvous_seals_the_first_conditional_declaration_without_deadlock()
    {
        var seeded = await SeedRunAsync();

        await using var producer = new NpgsqlConnection(_fixture.ConnectionString);
        await producer.OpenAsync();
        await using var producerTransaction = await producer.BeginTransactionAsync();
        await using (var takeLock = new NpgsqlCommand("SELECT workflow_run_data_completeness_lock(@team, @run)", producer, producerTransaction))
        {
            takeLock.Parameters.AddWithValue("team", seeded.TeamId);
            takeLock.Parameters.AddWithValue("run", seeded.RunId);
            await takeLock.ExecuteNonQueryAsync();
        }

        await using var terminal = new NpgsqlConnection(_fixture.ConnectionString);
        await terminal.OpenAsync();
        var terminalPid = terminal.ProcessID;
        await using var terminalCommand = new NpgsqlCommand("UPDATE workflow_run SET status = 'Failure' WHERE team_id = @team AND id = @run", terminal);
        terminalCommand.Parameters.AddWithValue("team", seeded.TeamId);
        terminalCommand.Parameters.AddWithValue("run", seeded.RunId);
        var terminalizing = terminalCommand.ExecuteNonQueryAsync();

        (await WaitsOnAdvisoryAsync(terminalPid)).ShouldBeTrue(
            "the terminal writer must have reached the shared rendezvous while holding its status update, or this test did not drive the lock ordering");
        terminalizing.IsCompleted.ShouldBeFalse("the status transaction is still inside its AFTER trigger and owns the run-row update");

        // This is the alleged cycle, exercised rather than inferred: T1 owns advisory and has no coverage header; T2
        // has already updated the run row and waits on advisory. The first declaration below still returns before T1
        // commits because its MVCC status SELECT does not wait, and its FK KEY SHARE locks are compatible with T2's
        // non-key status UPDATE (NO KEY UPDATE). If either operation acquired a conflicting row lock PostgreSQL would
        // report a deadlock here; merely putting a timeout around the test would not establish that missing wait edge.
        await using (var declare = new NpgsqlCommand("SELECT workflow_run_data_manifest_advance_covered(@team, @run, 'semantic-event', 1, 0, FALSE, ARRAY['model-call','harness-execution','harness-process-attempt','native-record']::text[], 1)", producer, producerTransaction))
        {
            declare.Parameters.AddWithValue("team", seeded.TeamId);
            declare.Parameters.AddWithValue("run", seeded.RunId);
            await declare.ExecuteNonQueryAsync();
        }
        terminalizing.IsCompleted.ShouldBeFalse("the first declaration returned while the terminal trigger still waited on T1, proving T1 never waited back on the run row");

        await producerTransaction.CommitAsync();
        (await terminalizing).ShouldBe(1);

        using var readScope = _fixture.BeginScope();
        var view = (await readScope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        view.IsTerminal.ShouldBeTrue();
        view.RequiredFacets.ShouldContain(WorkflowRunDataOwnerKinds.SemanticEvent);
        var coverage = await readScope.Resolve<CodeSpace.Core.Persistence.Db.CodeSpaceDbContext>().WorkflowRunDataCoverage.AsNoTracking()
            .SingleAsync(candidate => candidate.TeamId == seeded.TeamId && candidate.WorkflowRunId == seeded.RunId);
        coverage.State.ShouldBe(CodeSpace.Core.Persistence.Entities.WorkflowRunDataCoverageStates.Sealed);
        coverage.BaselineFacets.ShouldBe(RunDataManifestCoverage.RequiredFacets);

        async Task<bool> WaitsOnAdvisoryAsync(int processId)
        {
            await using var observer = new NpgsqlConnection(_fixture.ConnectionString);
            await observer.OpenAsync();

            for (var attempt = 0; attempt < 100; attempt++)
            {
                await using var command = new NpgsqlCommand("SELECT wait_event = 'advisory' FROM pg_stat_activity WHERE pid = @pid", observer);
                command.Parameters.AddWithValue("pid", processId);
                if (await command.ExecuteScalarAsync() is true) return true;
                await Task.Delay(20);
            }

            return false;
        }
    }

    /// <summary>
    /// A facet whose producer gave up before ever declaring anything is un-stated PERMANENTLY, even though the
    /// statement it un-states was minted indeterminate: the latch is what separates "nobody has declared one yet" from
    /// "one was declared and then withdrawn", and a later delta may not walk the second back to complete.
    /// </summary>
    [Fact]
    public async Task An_unstated_facet_absorbs_every_later_delta_even_when_it_was_only_ever_initialized()
    {
        var seeded = await SeedRunAsync();

        using var scope = _fixture.BeginScope();
        var writer = scope.Resolve<IRunDataCompletenessWriter>();
        (await writer.InitializeAsync(new RunDataManifestInitialization(seeded.TeamId, seeded.RunId), CancellationToken.None)).ShouldBeTrue();
        (await writer.UnstateExpectationAsync(seeded.TeamId, seeded.RunId, WorkflowRunDataOwnerKinds.ModelCall, CancellationToken.None))
            .ShouldBeTrue("a statement that was only initialized is not yet un-stated, so un-stating it revises something");
        (await writer.UnstateExpectationAsync(seeded.TeamId, seeded.RunId, WorkflowRunDataOwnerKinds.ModelCall, CancellationToken.None))
            .ShouldBeFalse("an already un-stated statement is left alone rather than re-revised");

        (await writer.AdvanceAsync(Advance(seeded, WorkflowRunDataOwnerKinds.ModelCall, expected: 1, present: 1), CancellationToken.None)).ShouldBeTrue();

        var view = (await scope.Resolve<IRunDataCompletenessReader>().ReadAsync(seeded.RunId, seeded.TeamId, CancellationToken.None)).ShouldNotBeNull();
        var modelCall = view.Facets.Single(facet => facet.Facet == WorkflowRunDataOwnerKinds.ModelCall);

        modelCall.ExpectedRecordCount.ShouldBeNull();
        modelCall.PresentRecordCount.ShouldBe(1);
        modelCall.Verdict.ShouldBe(WorkflowRunCaptureCompleteness.LegacyUnknown);
        modelCall.IsStrictlyReadable.ShouldBeFalse();
    }

    [Fact]
    public async Task Foreign_team_and_absent_run_are_indistinguishable()
    {
        var seeded = await SeedRunAsync();
        var (foreignTeamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var reader = scope.Resolve<IRunDataCompletenessReader>();

        (await reader.ReadAsync(seeded.RunId, foreignTeamId, CancellationToken.None)).ShouldBeNull();
        (await reader.ReadAsync(Guid.NewGuid(), seeded.TeamId, CancellationToken.None)).ShouldBeNull();
    }

    private static RunDataFacetAdvance Advance((Guid RunId, Guid TeamId, Guid UserId) seeded, string facet, long expected, long present) => new()
    {
        TeamId = seeded.TeamId,
        WorkflowRunId = seeded.RunId,
        Facet = facet,
        Expected = expected,
        Present = present,
    };

    private async Task<(Guid RunId, Guid TeamId, Guid UserId)> SeedRunAsync()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var workflowId = await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "data-completeness-" + Guid.NewGuid().ToString("N")[..6],
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        return (runId, teamId, userId);
    }

    private async Task AssertRawInsertRendezvousAsync((Guid RunId, Guid TeamId, Guid UserId) seeded, string table, string trigger, string insertSql)
    {
        const long pauseKey = 1870187;
        var function = trigger + "_function";
        await using var setup = new NpgsqlConnection(_fixture.ConnectionString);
        await setup.OpenAsync();
        await using (var command = new NpgsqlCommand($$"""
            CREATE OR REPLACE FUNCTION {{function}}() RETURNS trigger AS $body$
            BEGIN
                PERFORM pg_advisory_xact_lock({{pauseKey}});
                RETURN NEW;
            END;
            $body$ LANGUAGE plpgsql;
            CREATE TRIGGER {{trigger}} BEFORE INSERT ON {{table}}
            FOR EACH ROW EXECUTE FUNCTION {{function}}();
            """, setup)) await command.ExecuteNonQueryAsync();

        try
        {
            await using var blocker = new NpgsqlConnection(_fixture.ConnectionString);
            await blocker.OpenAsync();
            await using var blockerTransaction = await blocker.BeginTransactionAsync();
            await using (var command = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({pauseKey})", blocker, blockerTransaction))
                await command.ExecuteNonQueryAsync();

            await using var inserter = new NpgsqlConnection(_fixture.ConnectionString);
            await inserter.OpenAsync();
            var inserting = new NpgsqlCommand(insertSql, inserter).ExecuteNonQueryAsync();
            var insertWaits = await WaitsOnAdvisoryAsync(inserter.ProcessID);

            await using var terminal = new NpgsqlConnection(_fixture.ConnectionString);
            await terminal.OpenAsync();
            var terminalizing = new NpgsqlCommand($"UPDATE workflow_run SET status = 'Failure' WHERE team_id = '{seeded.TeamId}' AND id = '{seeded.RunId}'", terminal).ExecuteNonQueryAsync();
            var terminalWaits = await WaitsOnAdvisoryAsync(terminal.ProcessID);
            var terminalCompletedWhilePaused = terminalizing.IsCompleted;

            await blockerTransaction.RollbackAsync();
            insertWaits.ShouldBeTrue("the test pause must hold the raw INSERT after the production guard or this is not the target interleaving");
            terminalWaits.ShouldBeTrue("the raw guard must own the same run rendezvous as terminal sealing while its row is still paused before insertion");
            terminalCompletedWhilePaused.ShouldBeFalse("terminal sealing cannot overtake a raw applicability insert whose guard already admitted it");
            (await inserting).ShouldBe(1);
            (await terminalizing).ShouldBe(1);
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(_fixture.ConnectionString);
            await cleanup.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP TRIGGER IF EXISTS {trigger} ON {table}; DROP FUNCTION IF EXISTS {function}();", cleanup);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task<bool> WaitsOnAdvisoryAsync(int processId)
    {
        await using var observer = new NpgsqlConnection(_fixture.ConnectionString);
        await observer.OpenAsync();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            await using var command = new NpgsqlCommand("SELECT wait_event = 'advisory' FROM pg_stat_activity WHERE pid = @pid", observer);
            command.Parameters.AddWithValue("pid", processId);
            if (await command.ExecuteScalarAsync() is true) return true;
            await Task.Delay(20);
        }
        return false;
    }
}
