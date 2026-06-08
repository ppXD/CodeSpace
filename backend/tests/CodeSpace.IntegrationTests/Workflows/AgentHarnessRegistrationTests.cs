using Autofac;
using CodeSpace.Core.Services.Workflows.Agents;
using CodeSpace.IntegrationTests.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Verifies the harness seam is wired through the REAL CodeSpaceModule container — that the
/// <c>ISingletonDependency</c> marker registers <see cref="CodexCliHarness"/> as an <see cref="IAgentHarness"/>
/// and the registry resolves it by kind. Guards against the marker scan silently handing the registry
/// an empty set.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class AgentHarnessRegistrationTests
{
    private readonly PostgresFixture _fixture;

    public AgentHarnessRegistrationTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void Codex_harness_is_registered_and_resolvable_by_kind()
    {
        using var scope = _fixture.BeginScope();

        var registry = scope.Resolve<IAgentHarnessRegistry>();

        registry.Resolve(CodexCliHarness.HarnessKind).ShouldBeOfType<CodexCliHarness>();
        registry.All.ShouldContain(h => h.Kind == CodexCliHarness.HarnessKind);
    }
}
