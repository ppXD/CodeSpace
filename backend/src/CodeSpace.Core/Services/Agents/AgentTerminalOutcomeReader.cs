using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Reconciles a harness's own reported terminal outcome against the OS exit code it's paired with — a CLI can
/// exit 0 (the wrapping process didn't crash) while its own event stream reports the underlying turn/session
/// failed (Claude Code's <c>is_error: true</c> result line on e.g. a gateway 429; Codex's <c>turn.failed</c>).
/// Both harnesses already normalize that outcome into a terminal <see cref="AgentEventKind.Completed"/> or
/// <see cref="AgentEventKind.Error"/> event, so this reader is harness-agnostic: it finds the LAST such terminal
/// event and reports whether it was an Error, regardless of what the exit code says. Pure + stateless, mirroring
/// <see cref="AgentSessionIdReader"/>.
/// </summary>
public static class AgentTerminalOutcomeReader
{
    /// <summary>
    /// The <see cref="AgentRunResult.ExitReason"/> a harness's folder stamps when its CLI's OWN terminal event says the
    /// model refused the request as larger than its context window — a field or status the CLI wrote, never a phrase in
    /// the agent's prose. Harness-agnostic, like the rest of this reader: each folder knows its CLI's shape, and every
    /// consumer (the retry-cause classifier, and through it the agent.run node's retry verdict) keys on this one code.
    /// Pinned by a unit test (Rule 8) so the producers and the verdict cannot drift apart.
    /// </summary>
    public const string ContextWindowExceededExitReason = "context-window-exceeded";

    /// <summary>
    /// True when the last Completed-or-Error event in the stream is an Error — i.e. the harness itself reported
    /// the run failed, even if the OS exit code was 0. False when no such event exists (nothing to reconcile
    /// against, so the exit code alone decides) or the last one was Completed.
    /// </summary>
    public static bool ReportedFailure(IReadOnlyList<AgentEvent> events) =>
        events.LastOrDefault(e => IsTerminal(e.Kind))?.Kind == AgentEventKind.Error;

    /// <summary>The kinds that count as a harness-reported terminal outcome — the per-event predicate <see cref="AgentResultFold"/> keeps its LAST match of, so both readings agree by construction.</summary>
    public static bool IsTerminal(AgentEventKind kind) => kind is AgentEventKind.Completed or AgentEventKind.Error;
}
