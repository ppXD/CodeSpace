using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Adapter for one coding-agent harness (Codex CLI, Claude Code, Aider, OpenCode, …). This is the
/// normalization boundary: each implementation owns its OWN stable translation + parsing, but every
/// one speaks the same <see cref="AgentTask"/> in and <see cref="AgentEvent"/> / <see cref="AgentRunResult"/>
/// out — so the rest of CodeSpace behaves identically no matter which harness ran. We don't build a
/// harness; we adapt the best ones (Rule 7 / ISP — streaming/interactive variants land as siblings,
/// not by widening this).
///
/// Stateless + concurrency-safe: the registry resolves one instance and many runs share it.
/// </summary>
public interface IAgentHarness
{
    /// <summary>Stable harness tag the registry resolves by — e.g. "codex-cli", "claude-code".</summary>
    string Kind { get; }

    /// <summary>Pinned harness version this adapter targets, so the same workflow behaves identically over time (env-overridable per air-gapped operators).</summary>
    string Version { get; }

    /// <summary>Models this harness can drive — the catalog the UI offers (so it can't propose an impossible harness+model pair).</summary>
    IReadOnlyList<string> Models { get; }

    /// <summary>Translate the task envelope into a concrete sandbox invocation (executable + args + env + cwd + timeout) for an <see cref="ISandboxRunner"/>.</summary>
    SandboxSpec BuildInvocation(AgentTask task);

    /// <summary>
    /// Map one line of the harness's native output stream to zero or more normalized <see cref="AgentEvent"/>s.
    /// ONE native line can carry several content blocks (e.g. a Claude assistant turn with reasoning + a tool_use +
    /// text) — each becomes its own event, in stream order, so the durable log is FAITHFUL rather than first-block-only.
    /// Returns an empty list for lines that carry no event (blank / setup / unparseable noise) — never null.
    /// </summary>
    IReadOnlyList<AgentEvent> ParseEvents(string rawLine);

    /// <summary>Fold the run's normalized events + process exit code into the normalized <see cref="AgentRunResult"/>.</summary>
    AgentRunResult BuildResult(IReadOnlyList<AgentEvent> events, int exitCode);
}
