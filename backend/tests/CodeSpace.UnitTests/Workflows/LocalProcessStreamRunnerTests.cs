using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Agents.Sandbox.Runners;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// LocalProcessRunner satisfies the full <see cref="ISandboxStreamRunner"/> behavioral contract
/// (inherited from <see cref="SandboxStreamRunnerContractTests"/>) against a REAL OS process emitting
/// stdout over time.
/// </summary>
[Trait("Category", "Unit")]
[Collection("LocalProcessIdleWatchdog")]
public sealed class LocalProcessStreamRunnerTests : SandboxStreamRunnerContractTests
{
    protected override ISandboxStreamRunner StreamRunner { get; } = new LocalProcessRunner();
}
