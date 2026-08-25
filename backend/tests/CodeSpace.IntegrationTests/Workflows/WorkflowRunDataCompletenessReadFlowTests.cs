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
}
