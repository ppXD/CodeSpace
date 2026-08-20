using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Optional sibling capability of <see cref="IAgentHarness"/> (Rule 7 / ISP — never a widening of it): WHERE this
/// adapter's native stream spells the three run facts the platform reads off every run (session id, token usage,
/// model). The adapter owns its format, so it owns these spellings too; the shared readers keep the scan.
///
/// <para><b>What implementing it changes.</b> Two things, and only two. The readers use THIS table instead of
/// <see cref="AgentRunFactKeys.Fallback"/>, so a stream that names its conversation handle <c>conversation_id</c> or
/// nests usage under <c>metrics.tokens</c> is read at full fidelity without any edit to a shared reader. And a fact
/// that comes back null becomes a STATED absence — the adapter said where the fact lives and it was not there —
/// rather than an unestablished one, which is what <see cref="AgentRunFacts.UnestablishedFacts"/> reports for an
/// adapter that declared nothing.</para>
///
/// <para><b>Not implementing it is a supported choice, not a bug.</b> An adapter whose stream happens to use the
/// spellings in the fallback table extracts exactly what it always did. What it loses is the distinction above: its
/// null facts cannot be told apart from "we did not know where to look", so the executor logs them — a warm retry
/// cold-starting and a run billed as free are otherwise silent.</para>
///
/// <para><b>The obligation the declaration does not carry on its own.</b> These keys are read off
/// <c>AgentEvent.Data</c>, so <see cref="IAgentHarness.ParseEvents"/> must retain the fact-bearing native line's
/// structured root (or a sub-object holding the fact) on at least one event. A declaration over a payload the parse
/// discards extracts nothing.</para>
/// </summary>
public interface IAgentHarnessRunFactKeys
{
    /// <summary>This adapter's own spellings. Expected to be a constant — a stable property of the CLI's output format, committed and reviewed like the parse it belongs to, never derived at runtime.</summary>
    AgentRunFactKeys RunFactKeys { get; }
}
