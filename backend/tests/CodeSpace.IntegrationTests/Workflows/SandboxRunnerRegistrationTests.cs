using Autofac;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using CodeSpace.IntegrationTests.Infrastructure;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Verifies the sandbox seam is wired through the REAL CodeSpaceModule container — the part a unit
/// test (which news up the registry directly) can't prove: that the <c>ISingletonDependency</c>
/// marker registers <see cref="LocalProcessRunner"/> as an <see cref="ISandboxRunner"/>, and that
/// <see cref="ISandboxRunnerRegistry"/> resolves it by kind. If the marker scan ever stops picking
/// runners up, this fails instead of silently handing the registry an empty set.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SandboxRunnerRegistrationTests
{
    private readonly PostgresFixture _fixture;

    public SandboxRunnerRegistrationTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public void Local_runner_is_registered_and_resolvable_by_kind()
    {
        using var scope = _fixture.BeginScope();

        var registry = scope.Resolve<ISandboxRunnerRegistry>();

        registry.Resolve(LocalProcessRunner.LocalKind).ShouldBeOfType<LocalProcessRunner>();
        registry.All.ShouldContain(r => r.Kind == LocalProcessRunner.LocalKind);
    }
}
