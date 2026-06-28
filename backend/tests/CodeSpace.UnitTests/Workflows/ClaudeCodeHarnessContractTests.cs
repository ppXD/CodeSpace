using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Harnesses.Claude;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// ClaudeCodeHarness satisfies the universal <see cref="IAgentHarness"/> contract (inherited from
/// <see cref="AgentHarnessContractTests"/>) — the parity floor Codex already has. Its stream-json
/// parsing specifics (the type → AgentEventKind table, the CLI arg list, the env vars) live in
/// <c>ClaudeCodeHarnessTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ClaudeCodeHarnessContractTests : AgentHarnessContractTests
{
    protected override IAgentHarness Harness { get; } = new ClaudeCodeHarness();

    protected override (string Line, string ExpectedId) SessionIdLine =>
        ("""{"type":"result","subtype":"success","result":"done","is_error":false,"session_id":"sess-contract-claude"}""", "sess-contract-claude");
}
