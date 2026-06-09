using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// LocalProcessRunner satisfies the full <see cref="ISandboxRunner"/> behavioral contract (inherited
/// from <see cref="SandboxRunnerContractTests"/>) against a REAL OS process, plus its own kind tag.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LocalProcessRunnerTests : SandboxRunnerContractTests
{
    protected override ISandboxRunner Runner { get; } = new LocalProcessRunner();

    [Fact]
    public void Kind_is_local() => Runner.Kind.ShouldBe("local");
}
