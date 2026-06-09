using Autofac;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.Core.Services.Agents.Workspace;
using CodeSpace.Core.Services.Agents.Workspace.Providers;
using CodeSpace.IntegrationTests.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Verifies the workspace seam is wired through the REAL CodeSpaceModule container — what a unit test
/// (which news up the registry directly) can't prove: the <c>ISingletonDependency</c> marker registers
/// <see cref="LocalGitWorkspaceProvider"/> as an <see cref="IWorkspaceProvider"/>, and
/// <see cref="IWorkspaceProviderRegistry"/> resolves it by kind. If the marker scan ever stops picking
/// providers up, this fails instead of silently handing the registry an empty set. The provider's kind
/// matches the local sandbox runner's, so the executor can resolve both for the same backend.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkspaceProviderRegistrationTests
{
    private readonly PostgresFixture _fixture;

    public WorkspaceProviderRegistrationTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void Local_workspace_provider_is_registered_and_resolvable_by_kind()
    {
        using var scope = _fixture.BeginScope();

        var registry = scope.Resolve<IWorkspaceProviderRegistry>();

        registry.Resolve(LocalProcessRunner.LocalKind).ShouldBeOfType<LocalGitWorkspaceProvider>();
        registry.All.ShouldContain(p => p.Kind == LocalProcessRunner.LocalKind);
    }
}
