using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the generic, harness-agnostic <see cref="AgentTerminalOutcomeReader"/> — reconciles a harness's own
/// reported terminal outcome (its normalized Completed/Error event) against an OS exit code that can lie (a CLI
/// exits 0 while its own final event stream says the turn failed). Pure + stateless, mirroring
/// <see cref="AgentSessionIdReader"/>: scan events and answer a plain yes/no, never throwing on an odd shape.
/// </summary>
[Trait("Category", "Unit")]
public class AgentTerminalOutcomeReaderTests
{
    private static AgentEvent Event(AgentEventKind kind, string text = "") => new() { Kind = kind, Text = text };

    [Fact]
    public void Reports_no_failure_when_the_last_terminal_event_is_completed()
    {
        var events = new[] { Event(AgentEventKind.AssistantMessage, "working"), Event(AgentEventKind.Completed, "done") };

        AgentTerminalOutcomeReader.ReportedFailure(events).ShouldBeFalse();
    }

    [Fact]
    public void Reports_failure_when_the_last_terminal_event_is_an_error()
    {
        var events = new[] { Event(AgentEventKind.AssistantMessage, "working"), Event(AgentEventKind.Error, "API Error (429)") };

        AgentTerminalOutcomeReader.ReportedFailure(events).ShouldBeTrue();
    }

    [Fact]
    public void Reports_no_failure_when_no_completed_or_error_event_exists()
    {
        // Nothing to reconcile against — the exit code alone decides, so this must never manufacture a failure.
        var events = new[] { Event(AgentEventKind.AssistantMessage, "working"), Event(AgentEventKind.FileChanged, "src/a.ts") };

        AgentTerminalOutcomeReader.ReportedFailure(events).ShouldBeFalse();
    }

    [Fact]
    public void Reports_no_failure_for_an_empty_stream()
    {
        AgentTerminalOutcomeReader.ReportedFailure(Array.Empty<AgentEvent>()).ShouldBeFalse();
    }

    [Fact]
    public void The_last_completed_or_error_event_wins_over_an_earlier_one_of_the_opposite_kind()
    {
        // A mid-stream item error that the turn recovers from, followed by a clean turn.completed, must NOT
        // flag the run as failed — only the LATEST terminal-kind event decides (Codex's per-item error vs its
        // trailing turn.completed).
        var recovered = new[] { Event(AgentEventKind.Error, "tool call failed"), Event(AgentEventKind.Completed, "turn complete") };

        AgentTerminalOutcomeReader.ReportedFailure(recovered).ShouldBeFalse();

        // And the reverse: a clean turn earlier, then a later failure, must be reported.
        var thenFailed = new[] { Event(AgentEventKind.Completed, "turn complete"), Event(AgentEventKind.Error, "unexpected status 401") };

        AgentTerminalOutcomeReader.ReportedFailure(thenFailed).ShouldBeTrue();
    }
}
