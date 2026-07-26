using Autofac;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Tasks;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration: P5-7's spec-preview lane through the REAL mediator pipeline (auth middleware + handler +
/// <c>ITaskSpecCompiler</c> DI wiring) over real Postgres. The integration container registers no structured
/// LLM provider with a team pool model, so the documented 兜底 IS the observable behavior: a clean
/// <c>{ suggestion: null }</c> degrade — never a throw, never a scaffold. The suggestion-producing paths (model
/// reply → mapping matrix, grounding fold, fault degrades) are pinned at the unit tier with fakes at the honest
/// <c>IStructuredLLMClient</c> seam; suggestion QUALITY is M-track calibration, not a mechanism gate.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TaskSpecPreviewFlowTests
{
    private readonly PostgresFixture _fixture;

    public TaskSpecPreviewFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Spec_preview_degrades_to_a_null_suggestion_when_no_structured_model_exists()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin);

        var result = await scope.Resolve<IMediator>().Send(new CompileTaskSpecCommand { Goal = "fix the parser bug and make the tests pass" }, CancellationToken.None);

        result.ShouldNotBeNull("the endpoint always answers — default-ON with no flag, degrade is a shape not an error");
        result.Suggestion.ShouldBeNull("no structured pool model in this deployment → nothing suggested (the 兜底)");
        result.Grounded.ShouldBeFalse("no repository was named");
    }

    [Fact]
    public async Task A_cross_team_repository_yields_no_grounding_and_still_answers()
    {
        // Tenancy: the repo id belongs to NOBODY (a random guid) — grounding resolves team-scoped to null and the
        // preview still answers cleanly (fail-closed on the read, fail-soft on the reply).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin);

        var result = await scope.Resolve<IMediator>().Send(new CompileTaskSpecCommand { Goal = "ship it", RepositoryId = Guid.NewGuid() }, CancellationToken.None);

        result.Grounded.ShouldBeFalse("an unknown / cross-team repo grounds nothing — never a cross-team read");
        result.Suggestion.ShouldBeNull();
    }
}
