using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the llm.complete node's config → sampling-knob mapping (PR-5): each Dify knob (top_p / frequency_penalty /
/// presence_penalty / stop) flows from node config into LlmSamplingOptions, and an absent set produces null so the
/// request is byte-identical to the temperature-only path.
/// </summary>
[Trait("Category", "Unit")]
public class LlmCompleteNodeSamplingTests
{
    private static IReadOnlyDictionary<string, JsonElement> Config(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void All_four_knobs_flow_from_config()
    {
        var s = LlmCompleteNode.BuildSampling(Config("""{"topP":0.9,"frequencyPenalty":0.5,"presencePenalty":-0.3,"stop":["END","STOP"]}"""));

        s.ShouldNotBeNull();
        s!.TopP.ShouldBe(0.9);
        s.FrequencyPenalty.ShouldBe(0.5);
        s.PresencePenalty.ShouldBe(-0.3);
        s.Stop!.Count.ShouldBe(2);
    }

    [Fact]
    public void A_single_knob_sets_only_that_field()
    {
        var s = LlmCompleteNode.BuildSampling(Config("""{"topP":0.7}"""));

        s.ShouldNotBeNull();
        s!.TopP.ShouldBe(0.7);
        s.FrequencyPenalty.ShouldBeNull();
        s.Stop.ShouldBeNull();
    }

    [Fact]
    public void No_knobs_yields_null_so_the_request_is_unchanged()
    {
        LlmCompleteNode.BuildSampling(Config("""{"provider":"Anthropic","temperature":0.2}""")).ShouldBeNull();
        LlmCompleteNode.BuildSampling(Config("{}")).ShouldBeNull();
    }

    [Fact]
    public void An_empty_or_non_string_stop_array_is_ignored()
    {
        LlmCompleteNode.BuildSampling(Config("""{"stop":[]}""")).ShouldBeNull("an empty stop array is not a real knob");
        LlmCompleteNode.BuildSampling(Config("""{"stop":["ok",5]}""")).ShouldNotBeNull().Stop!.ShouldHaveSingleItem().ShouldBe("ok");
    }
}
