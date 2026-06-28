using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the generic, tolerant <see cref="AgentSessionIdReader"/> — the harness-agnostic primitive that
/// surfaces a run's native CLI session/thread id from its normalized events (Claude's <c>session_id</c> off the
/// result line, Codex's <c>thread_id</c> off <c>thread.started</c>), so a later rerun can CONTINUE the prior
/// conversation. Pure + stateless, mirroring <see cref="AgentTokenUsageReader"/>: scan the events' structured
/// payload for a recognizable id across the known key aliases + the <c>msg</c> envelope, and return null (never a
/// fabricated value) when none is present.
/// </summary>
[Trait("Category", "Unit")]
public class AgentSessionIdReaderTests
{
    private static AgentEvent Event(string dataJson) => new()
    {
        Kind = AgentEventKind.Warning,
        Text = "",
        Data = JsonDocument.Parse(dataJson).RootElement.Clone(),
    };

    private static AgentEvent EventWithoutData() => new() { Kind = AgentEventKind.AssistantMessage, Text = "hi" };

    [Fact]
    public void Reads_the_claude_session_id_off_an_events_data()
    {
        var events = new[] { Event("""{"type":"result","subtype":"success","session_id":"sess-claude-abc","is_error":false}""") };

        AgentSessionIdReader.TryRead(events).ShouldBe("sess-claude-abc");
    }

    [Fact]
    public void Reads_the_codex_thread_id_off_an_events_data()
    {
        var events = new[] { Event("""{"type":"thread.started","thread_id":"thr-codex-xyz"}""") };

        AgentSessionIdReader.TryRead(events).ShouldBe("thr-codex-xyz");
    }

    [Fact]
    public void Reads_an_id_nested_under_the_msg_envelope()
    {
        // Codex has used both a top-level shape and a {msg:{…}} envelope — the reader must tolerate the nesting,
        // exactly as AgentTokenUsageReader does for usage.
        var events = new[] { Event("""{"msg":{"type":"session.created","session_id":"sess-nested"}}""") };

        AgentSessionIdReader.TryRead(events).ShouldBe("sess-nested");
    }

    [Fact]
    public void Returns_the_first_id_present_across_the_stream()
    {
        // The session id is constant for a run; the FIRST carrier wins (Codex's thread.started leads the stream).
        var events = new[]
        {
            Event("""{"type":"thread.started","thread_id":"thr-first"}"""),
            Event("""{"type":"assistant","message":"working"}"""),
            Event("""{"type":"thread.started","thread_id":"thr-second"}"""),
        };

        AgentSessionIdReader.TryRead(events).ShouldBe("thr-first");
    }

    [Fact]
    public void Returns_null_when_no_event_carries_an_id()
    {
        var events = new[]
        {
            Event("""{"type":"assistant","message":"working"}"""),
            Event("""{"type":"result","subtype":"success","is_error":false}"""),
        };

        AgentSessionIdReader.TryRead(events).ShouldBeNull("no session/thread id in the stream → null, never a fabricated value");
    }

    [Fact]
    public void Ignores_an_empty_id_and_a_non_string_id()
    {
        var events = new[]
        {
            Event("""{"session_id":""}"""),
            Event("""{"thread_id":12345}"""),
        };

        AgentSessionIdReader.TryRead(events).ShouldBeNull("an empty string or a non-string id is not a usable session id");
    }

    [Fact]
    public void Tolerates_events_with_no_data_and_an_empty_stream()
    {
        AgentSessionIdReader.TryRead(new[] { EventWithoutData() }).ShouldBeNull();
        AgentSessionIdReader.TryRead(Array.Empty<AgentEvent>()).ShouldBeNull();
    }
}
