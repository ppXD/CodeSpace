using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// 🟢 Unit: the generic, tolerant <see cref="AgentModelReader"/> — the primitive that surfaces the model a run ACTUALLY
/// ran from its normalized events, so an UNPINNED run still reports what it used instead of a blank cell. Pure +
/// stateless, mirroring <see cref="AgentSessionIdReader"/>: scan the events' structured payload for a model key (also
/// under the <c>msg</c> envelope) and return null (never a fabricated value) when none is present.
///
/// <para>Only the Claude case below is grounded in a real stream: Claude Code names the model on its <c>init</c> line.
/// The other payloads are CONSTRUCTED to exercise the reader's tolerance — the event type in them is decoration, since
/// the reader keys on the <c>model</c>/<c>model_name</c> key and never on a type. In particular Codex's
/// <c>exec --json</c> stream names no model at all (<c>CodexHarness.ReadSessionFrame</c>), so no test here may be read
/// as pinning a Codex behaviour.</para>
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
    public void Reads_a_model_key_off_an_event_type_it_knows_nothing_about()
    {
        // A CONSTRUCTED payload, not a captured one: the reader has no type table, so an arbitrary type carrying a
        // model key must still yield it. Codex prints no model, so this pins the reader's tolerance, not a harness.
        var events = new[] { Event("""{"type":"thread.started","thread_id":"thr-xyz","model":"gpt-5-codex"}""") };

        AgentModelReader.TryRead(events).ShouldBe("gpt-5-codex");
    }

    [Fact]
    public void Reads_a_model_nested_under_the_msg_envelope()
    {
        // Codex has used both a top-level shape and a {msg:{…}} envelope for its events, so the reader tolerates the
        // nesting exactly as the session-id + token-usage readers do. Constructed: no Codex frame carries a model.
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
