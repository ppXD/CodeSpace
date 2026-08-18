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

    /// <summary>
    /// Open a fresh <see cref="IAgentEventFolder"/> for ONE run: the accumulator this harness folds its
    /// <see cref="AgentRunResult"/> from, which the executor drives event-by-event and then asks for the result.
    /// The harness owns the reduction's SHAPE — retention has to be O(1) (a long agent's whole event list exhausted
    /// the heap and failed a run that had actually succeeded), but WHAT is retained is this harness's own business,
    /// so a reduction only one harness needs never becomes a field on a type every harness shares (Rule 7 / ISP).
    /// </summary>
    IAgentEventFolder CreateFolder();
}

/// <summary>
/// Fold a stream that is already fully in hand through the same harness reduction the streaming executor drives
/// event-by-event. INTERNAL on purpose: the whole point of narrowing the seam to <see cref="IAgentHarness.CreateFolder"/>
/// is that re-materializing a run's events becomes a visible decision, and a public convenience over the narrow
/// interface would hand that back. The guard is stronger than before — the interface carries no <c>BuildResult</c>
/// at all now, so this cannot be reached by an overload silently binding to a list; naming it is the only way in.
/// Only the test assemblies (which genuinely hold a finished stream — replay fixtures, the fake-CLI drift detector,
/// the harness unit suites) reach it, via InternalsVisibleTo.
/// </summary>
internal static class AgentHarnessFoldExtensions
{
    internal static AgentRunResult BuildResult(this IAgentHarness harness, IReadOnlyList<AgentEvent> events, int exitCode) => harness.Folded(events).BuildResult(AgentRunFacts.From(events), exitCode);

    /// <summary>The folder this harness would have accumulated over the whole stream — for the callers that need the FOLDER rather than the result (the executor's own mapping, driven with a finished stream).</summary>
    internal static IAgentEventFolder Folded(this IAgentHarness harness, IReadOnlyList<AgentEvent> events)
    {
        var folder = harness.CreateFolder();

        foreach (var normalized in events) folder.Add(normalized);

        return folder;
    }
}
