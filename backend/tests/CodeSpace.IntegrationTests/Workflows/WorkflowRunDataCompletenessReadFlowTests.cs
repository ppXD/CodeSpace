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
