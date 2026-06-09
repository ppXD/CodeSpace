using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Codex;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// CodexHarness satisfies the universal <see cref="IAgentHarness"/> contract (inherited from
/// <see cref="AgentHarnessContractTests"/>). Its native-stream parsing specifics — the exact JSONL
/// type → AgentEventKind table, the CLI arg list, the version env var — live in <c>CodexHarnessTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CodexHarnessContractTests : AgentHarnessContractTests
{
    protected override IAgentHarness Harness { get; } = new CodexHarness();
}
