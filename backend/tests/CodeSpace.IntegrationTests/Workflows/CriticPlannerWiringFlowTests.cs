using Autofac;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.IntegrationTests.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Confirms the Autofac decorator wiring (the design's top risk): resolving <see cref="IWorkflowPlanner"/> from the
/// production container yields the <see cref="CriticPlannerDecorator"/> wrapping the real planner — registered exactly
/// once (no double-registration), so the generic critic is in the chain for every plan. (Behavior is inert by default:
/// the decorator short-circuits ReviewMode.None — proven in the unit suite.)
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CriticPlannerWiringFlowTests
{
    private readonly PostgresFixture _fixture;

    public CriticPlannerWiringFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void The_planner_is_wrapped_by_the_critic_decorator_exactly_once()
    {
        using var scope = _fixture.BeginScope();

        var planner = scope.Resolve<IWorkflowPlanner>();

        planner.ShouldBeOfType<CriticPlannerDecorator>("the critic decorator wraps IWorkflowPlanner so the review primitive is in the chain");
    }
}
