namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// OPTIONAL sibling capability of <see cref="IAgentHarness"/> (Rule 7 — a capability a harness opts into, not a
/// widening of the core interface): a harness whose LIVE event stream does NOT name the model, but whose captured
/// session transcript DOES, implements this so the executor can backfill <see cref="Messages.Agents.AgentRunResult.Model"/>
/// as a LAST resort — only after <see cref="AgentModelReader"/> found none on the stream. Codex is the case: its threaded
/// <c>--json</c> stream omits the model (it's chosen server-side), recording it only in the on-disk session rollout,
/// whereas Claude names it on its <c>system/init</c> line (so Claude never needs this — the reader already has it). Pure +
/// tolerant + MUST never throw (returns null on any malformed transcript), mirroring <see cref="AgentModelReader"/>.
/// </summary>
public interface IAgentTranscriptModelSource
{
    /// <summary>The model the run ACTUALLY ran, read from its captured session transcript, or null when the transcript names none.</summary>
    string? TryReadModelFromTranscript(string transcript);
}
