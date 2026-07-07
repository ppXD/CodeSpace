using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// D3b-i: pins <see cref="AgentTokenUsageReader"/> — the tolerant primitive that extracts the cost-accounting
/// token figure from a run's events (populating <see cref="AgentRunResult.TokenUsage"/>, consumed by the
/// per-team budget cap). It must read the real shapes each provider reports (Codex nested
/// <c>info.total_token_usage</c>, Claude <c>usage</c>, an OpenAI-compatible gateway's <c>prompt/completion_tokens</c>),
/// prefer the cumulative total, take the LAST-emitted count, and return null (never a misleading half-count)
/// when usage is absent or partial.
/// </summary>
[Trait("Category", "Unit")]
public class AgentTokenUsageReaderTests
{
    private static AgentEvent Ev(string json) =>
        new() { Kind = AgentEventKind.Warning, Text = "", Data = JsonDocument.Parse(json).RootElement.Clone() };

    private static AgentEvent NoData() => new() { Kind = AgentEventKind.AssistantMessage, Text = "hi" };

    [Fact]
    public void Reads_codex_nested_total_token_usage()
    {
        var usage = AgentTokenUsageReader.TryRead(new[]
        {
            Ev("{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":1200,\"output_tokens\":340}}}"),
        });

        usage.ShouldNotBeNull();
        usage!.InputTokens.ShouldBe(1200);
        usage.OutputTokens.ShouldBe(340);
    }

    [Fact]
    public void Prefers_the_cumulative_total_over_a_per_turn_delta()
    {
        // Codex's token_count carries BOTH a cumulative total and the last turn's delta — the run figure is the total.
        var usage = AgentTokenUsageReader.TryRead(new[]
        {
            Ev("{\"info\":{\"last_token_usage\":{\"input_tokens\":10,\"output_tokens\":5},\"total_token_usage\":{\"input_tokens\":1200,\"output_tokens\":340}}}"),
        });

        usage!.InputTokens.ShouldBe(1200, "the cumulative total wins, not the last-turn delta");
        usage.OutputTokens.ShouldBe(340);
    }

    [Fact]
    public void Reads_claude_usage_object()
    {
        var usage = AgentTokenUsageReader.TryRead(new[]
        {
            Ev("{\"type\":\"result\",\"result\":\"done\",\"usage\":{\"input_tokens\":900,\"output_tokens\":120,\"cache_read_input_tokens\":50}}"),
        });

        usage!.InputTokens.ShouldBe(900);
        usage.OutputTokens.ShouldBe(120);
    }

    [Fact]
    public void Reads_openai_gateway_prompt_completion_aliases()
    {
        var usage = AgentTokenUsageReader.TryRead(new[]
        {
            Ev("{\"usage\":{\"prompt_tokens\":777,\"completion_tokens\":88}}"),
        });

        usage!.InputTokens.ShouldBe(777);
        usage.OutputTokens.ShouldBe(88);
    }

    [Fact]
    public void Reads_a_flat_top_level_usage()
    {
        AgentTokenUsageReader.TryRead(new[] { Ev("{\"input_tokens\":3,\"output_tokens\":4}") })!.InputTokens.ShouldBe(3);
    }

    [Fact]
    public void Reads_a_msg_enveloped_usage()
    {
        var usage = AgentTokenUsageReader.TryRead(new[]
        {
            Ev("{\"msg\":{\"type\":\"token_count\",\"usage\":{\"input_tokens\":21,\"output_tokens\":12}}}"),
        });

        usage!.InputTokens.ShouldBe(21);
        usage.OutputTokens.ShouldBe(12);
    }

    [Fact]
    public void Takes_the_last_emitted_usage_for_a_cumulative_stream()
    {
        // Codex emits a growing token_count each turn — the run total is the LAST one.
        var usage = AgentTokenUsageReader.TryRead(new[]
        {
            Ev("{\"input_tokens\":100,\"output_tokens\":10}"),
            Ev("{\"type\":\"agent_message\",\"message\":\"work\"}"),
            Ev("{\"input_tokens\":250,\"output_tokens\":40}"),
        });

        usage!.InputTokens.ShouldBe(250, "the last-emitted cumulative count is the run total");
        usage.OutputTokens.ShouldBe(40);
    }

    [Fact]
    public void Returns_null_when_no_event_carries_usage()
    {
        AgentTokenUsageReader.TryRead(new[]
        {
            Ev("{\"type\":\"agent_message\",\"message\":\"hi\"}"),
            NoData(),
        }).ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_a_partial_usage_with_only_one_figure()
    {
        // A half-count (input but no output) is worse than nothing for cost accounting — reject it.
        AgentTokenUsageReader.TryRead(new[] { Ev("{\"usage\":{\"input_tokens\":500}}") }).ShouldBeNull();
    }

    [Fact]
    public void Returns_null_for_an_empty_event_list()
    {
        AgentTokenUsageReader.TryRead(System.Array.Empty<AgentEvent>()).ShouldBeNull();
    }
}
