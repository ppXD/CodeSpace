using System.Text.Json;
using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the map planner plan reader — a flow.map's items come in TWO generic shapes and BOTH must render the plan
/// card. An OBJECT subtask (a <c>plan.author</c> plan: id+title) and a plain STRING subtask (a simpler planner whose
/// <c>json.subtasks</c> is a string array) both project to a <c>JournalSubtask</c>; a non-object/non-string element is
/// skipped. Pins the string case a real map run hit — the beat counted "Planned 5 subtasks" but the card was EMPTY
/// because the reader dropped every non-object element. Pure over the subtasks JSON — no DB.
/// </summary>
[Trait("Category", "Unit")]
public class MapPlannerFactsSourceTests
{
    [Fact]
    public void Reads_object_subtasks_by_id_and_title()
    {
        var result = MapPlannerFactsSource.ReadSubtasks(Arr("[{\"id\":\"a\",\"title\":\"First\"},{\"id\":\"b\",\"title\":\"Second\"}]"));

        result.Select(s => s.SubtaskId).ShouldBe(new[] { "a", "b" });
        result.Select(s => s.Title).ShouldBe(new[] { "First", "Second" });
    }

    [Fact]
    public void Reads_string_subtasks_with_a_positional_id_and_the_string_as_title()
    {
        // A real map run's planner emitted json.subtasks as STRINGS — the beat counted them but the card was empty until
        // the reader handled this shape. The string IS the title; its id is the positional index (a bare string has none).
        var result = MapPlannerFactsSource.ReadSubtasks(Arr("[\"分析當前架構\",\"研究競品\"]"));

        result.Select(s => s.Title).ShouldBe(new[] { "分析當前架構", "研究競品" });
        result.Select(s => s.SubtaskId).ShouldBe(new[] { "item-0", "item-1" });
    }

    [Fact]
    public void Skips_a_non_object_non_string_element()
    {
        MapPlannerFactsSource.ReadSubtasks(Arr("[42, \"real\", null]")).Select(s => s.Title).ShouldBe(new[] { "real" });
    }

    [Fact]
    public void Reads_the_authoring_model_call_off_the_planner_outputs()
    {
        // A flow.map planner stamps model + tokens on its node OUTPUTS even when no separate interaction record exists, so
        // the plan beat can always attribute HOW it was planned. Cost prefers the recorded value.
        var call = MapPlannerFactsSource.ModelCallFromOutputs(Arr("{\"model\":\"metis-coder-max\",\"inputTokens\":352,\"outputTokens\":1226,\"costUsd\":0.05}"));

        call.ShouldNotBeNull();
        call!.Model.ShouldBe("metis-coder-max");
        call.Tokens.ShouldBe(1578, "input + output");
        call.CostUsd.ShouldBe(0.05m, "the recorded cost wins");
        call.Purpose.ShouldBe("plan.author");
        call.Status.ShouldBe("completed");
    }

    [Fact]
    public void An_unpriced_planner_with_no_recorded_cost_reads_null_cost_but_keeps_tokens()
    {
        var call = MapPlannerFactsSource.ModelCallFromOutputs(Arr("{\"model\":\"metis-coder-max\",\"inputTokens\":352,\"outputTokens\":1226}"));

        call.ShouldNotBeNull();
        call!.Tokens.ShouldBe(1578);
        call.CostUsd.ShouldBeNull("metis-coder-max is unpriced and no cost was recorded → fail-open null");
    }

    [Fact]
    public void Outputs_with_no_model_carry_no_model_call()
    {
        MapPlannerFactsSource.ModelCallFromOutputs(Arr("{\"json\":{\"subtasks\":[]}}")).ShouldBeNull();
    }

    private static JsonElement Arr(string json) => JsonDocument.Parse(json).RootElement;
}
