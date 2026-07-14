using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the generic, tolerant <see cref="AgentModelReader"/> — the harness-agnostic primitive that surfaces the
/// model a run ACTUALLY ran from its normalized events (Claude names it on its <c>init</c> line, Codex on
/// <c>thread.started</c> / <c>turn.started</c>), so an UNPINNED run still reports what it used instead of a blank cell.
/// Pure + stateless, mirroring <see cref="AgentSessionIdReader"/>: scan the events' structured payload for a model key
/// (also under the <c>msg</c> envelope) and return null (never a fabricated value) when none is present.
/// </summary>
[Trait("Category", "Unit")]
public class AgentModelReaderTests
{
    private static AgentEvent Event(string dataJson) => new()
    {
        Kind = AgentEventKind.Warning,
        Text = "",
        Data = JsonDocument.Parse(dataJson).RootElement.Clone(),
    };

    private static AgentEvent EventWithoutData() => new() { Kind = AgentEventKind.AssistantMessage, Text = "hi" };

    [Fact]
    public void Reads_the_claude_model_off_its_init_line()
    {
        var events = new[] { Event("""{"type":"system","subtype":"init","model":"claude-opus-4-8","session_id":"sess-abc"}""") };

        AgentModelReader.TryRead(events).ShouldBe("claude-opus-4-8");
    }

    [Fact]
    public void Reads_the_codex_model_off_thread_started()
    {
        var events = new[] { Event("""{"type":"thread.started","thread_id":"thr-xyz","model":"gpt-5-codex"}""") };

        AgentModelReader.TryRead(events).ShouldBe("gpt-5-codex");
    }

    [Fact]
    public void Reads_a_model_nested_under_the_msg_envelope()
    {
        // Codex has used both a top-level shape and a {msg:{…}} envelope — the reader tolerates the nesting exactly
        // as the session-id + token-usage readers do.
        var events = new[] { Event("""{"msg":{"type":"turn.started","model":"gpt-5-codex-nested"}}""") };

        AgentModelReader.TryRead(events).ShouldBe("gpt-5-codex-nested");
    }

    [Fact]
    public void Reads_the_model_name_alias()
    {
        var events = new[] { Event("""{"type":"config","model_name":"claude-sonnet-4-6"}""") };

        AgentModelReader.TryRead(events).ShouldBe("claude-sonnet-4-6");
    }

    [Fact]
    public void Returns_the_first_model_present_across_the_stream()
    {
        // A run's model is constant; the FIRST carrier wins (the leading config/init line).
        var events = new[]
        {
            Event("""{"type":"thread.started","model":"model-first"}"""),
            Event("""{"type":"assistant","message":"working"}"""),
            Event("""{"type":"turn.started","model":"model-second"}"""),
        };

        AgentModelReader.TryRead(events).ShouldBe("model-first");
    }

    [Fact]
    public void Returns_null_when_no_event_carries_a_model()
    {
        var events = new[]
        {
            Event("""{"type":"assistant","message":"working"}"""),
            Event("""{"type":"result","subtype":"success","is_error":false}"""),
        };

        AgentModelReader.TryRead(events).ShouldBeNull("no model in the stream → null, never a fabricated value");
    }

    [Fact]
    public void Ignores_an_empty_model_and_a_non_string_model()
    {
        var events = new[]
        {
            Event("""{"model":""}"""),
            Event("""{"model":12345}"""),
        };

        AgentModelReader.TryRead(events).ShouldBeNull("an empty string or a non-string model is not a usable model");
    }

    [Fact]
    public void Tolerates_events_with_no_data_and_an_empty_stream()
    {
        AgentModelReader.TryRead(new[] { EventWithoutData() }).ShouldBeNull();
        AgentModelReader.TryRead(Array.Empty<AgentEvent>()).ShouldBeNull();
    }
}
