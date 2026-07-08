using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

[Trait("Category", "Unit")]
public class AgentRunExecutorMappingTests
{
    private static SandboxResult Sandbox(SandboxStatus status) => new() { Status = status, ExitCode = -1, Stdout = "", Stderr = "" };

    [Fact]
    public void A_stalled_sandbox_maps_to_needs_review_blocked()
    {
        // C3: a stalled run (no output for the idle window — likely a nested prompt) is surfaced for a human as
        // NeedsReview(Blocked), not buried as a bare timeout. The harness is not consulted for a non-exit terminal.
        var result = AgentRunExecutor.MapSandboxResult(Sandbox(SandboxStatus.Stalled), harness: null!, System.Array.Empty<AgentEvent>());

        result.Status.ShouldBe(AgentRunStatus.NeedsReview);
        result.CompletionDisposition.ShouldBe(CompletionDisposition.Blocked);
        result.ExitReason.ShouldBe("stalled");
        result.Error.ShouldContain("stalled");
    }

    [Fact]
    public void A_timed_out_sandbox_still_maps_to_timed_out()
    {
        var result = AgentRunExecutor.MapSandboxResult(Sandbox(SandboxStatus.TimedOut), harness: null!, System.Array.Empty<AgentEvent>());

        result.Status.ShouldBe(AgentRunStatus.TimedOut);
        result.ExitReason.ShouldBe("timed-out");
    }

    [Theory]
    [InlineData(SandboxStatus.TimedOut)]
    [InlineData(SandboxStatus.Stalled)]
    public void A_forced_terminal_still_captures_the_tokens_the_agent_burned(SandboxStatus status)
    {
        // A timed-out / stalled agent consumed budget before we killed it — its usage must be captured from the
        // events, so the spend shows on the run regardless of outcome (parity with the harness fold for a clean exit).
        var events = new[] { UsageEvent(input: 1200, output: 340) };

        var result = AgentRunExecutor.MapSandboxResult(Sandbox(status), harness: null!, events);

        result.TokenUsage.ShouldNotBeNull();
        result.TokenUsage!.InputTokens.ShouldBe(1200);
        result.TokenUsage.OutputTokens.ShouldBe(340);
    }

    private static AgentEvent UsageEvent(int input, int output)
    {
        using var doc = System.Text.Json.JsonDocument.Parse($"{{\"input_tokens\":{input},\"output_tokens\":{output}}}");
        return new AgentEvent { Kind = AgentEventKind.AssistantMessage, Text = "", Data = doc.RootElement.Clone() };
    }

    [Theory]
    [InlineData(SandboxStatus.TimedOut)]
    [InlineData(SandboxStatus.Stalled)]
    public void A_forced_terminal_captures_a_session_id_the_events_carried(SandboxStatus status)
    {
        // A killed-before-completion agent may still have emitted its harness's early session-bearing lifecycle
        // event (Claude's system/init line, Codex's thread.started) before the kill — capture it so a later RETRY
        // can warm-resume the conversation instead of always cold-starting.
        var events = new[] { SessionIdEvent("sess-forced-terminal-1") };

        var result = AgentRunExecutor.MapSandboxResult(Sandbox(status), harness: null!, events);

        result.SessionId.ShouldBe("sess-forced-terminal-1");
    }

    [Theory]
    [InlineData(SandboxStatus.TimedOut)]
    [InlineData(SandboxStatus.Stalled)]
    public void A_forced_terminal_with_no_session_bearing_event_leaves_session_id_null(SandboxStatus status)
    {
        // Byte-identical to before this fix when the stream carried no id at all (e.g. the process was killed
        // before even its first lifecycle line) — never a fabricated value.
        var result = AgentRunExecutor.MapSandboxResult(Sandbox(status), harness: null!, System.Array.Empty<AgentEvent>());

        result.SessionId.ShouldBeNull();
    }

    private static AgentEvent SessionIdEvent(string sessionId)
    {
        using var doc = System.Text.Json.JsonDocument.Parse($"{{\"session_id\":\"{sessionId}\"}}");
        return new AgentEvent { Kind = AgentEventKind.Started, Text = "Session started", Data = doc.RootElement.Clone() };
    }
}
