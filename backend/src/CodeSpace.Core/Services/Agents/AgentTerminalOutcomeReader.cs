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
    /// True when the last Completed-or-Error event in the stream is an Error — i.e. the harness itself reported
    /// the run failed, even if the OS exit code was 0. False when no such event exists (nothing to reconcile
    /// against, so the exit code alone decides) or the last one was Completed.
    /// </summary>
    public static bool ReportedFailure(IReadOnlyList<AgentEvent> events) =>
        events.LastOrDefault(e => e.Kind is AgentEventKind.Completed or AgentEventKind.Error)?.Kind == AgentEventKind.Error;
}
