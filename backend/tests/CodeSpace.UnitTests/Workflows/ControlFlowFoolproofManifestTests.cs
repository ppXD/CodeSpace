using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The control-flow nodes (flow.decision, flow.map, logic.merge) render their behaviour-fork enums as
/// radioCards where EVERY option carries a plain-language consequence — the reader must understand each fork
/// (who answers, what happens when a branch fails, which upstream wins) before choosing. These are
/// presentation-only hints the engine ignores: radioCards/segmented store the SAME enum string the
/// &lt;select&gt; did, so the config VALUE shape is unchanged and every stored workflow is unaffected.
/// </summary>
[Trait("Category", "Unit")]
public class ControlFlowFoolproofManifestTests
{
    private static JsonElement DecisionConfig => new FlowDecisionNode().Manifest.ConfigSchema;
    private static JsonElement MapConfig => new FlowMapNode().Manifest.ConfigSchema;
    private static JsonElement MergeConfig => new LogicMergeNode().Manifest.ConfigSchema;

    public static IEnumerable<object[]> BehaviourForks()
    {
        yield return new object[] { "flow.decision", DecisionConfig, "decisionType" };
        yield return new object[] { "flow.decision", DecisionConfig, "policy" };
        yield return new object[] { "flow.map", MapConfig, "errorHandling" };
        yield return new object[] { "logic.merge", MergeConfig, "strategy" };
    }

    [Theory]
    [MemberData(nameof(BehaviourForks))]
    public void Behaviour_fork_is_radiocards_and_explains_every_option(string node, JsonElement config, string forkField)
    {
        var prop = config.GetProperty("properties").GetProperty(forkField);

        prop.GetProperty("x-control").GetString().ShouldBe("radioCards", $"{node}.{forkField} is a behaviour fork — render it as stacked cards, not a bare dropdown");

        prop.TryGetProperty("x-optionConsequence", out var cons).ShouldBeTrue($"{node}.{forkField} must declare x-optionConsequence so no option is a mystery");
        prop.TryGetProperty("x-enumLabels", out var labels).ShouldBeTrue($"{node}.{forkField} must declare x-enumLabels");

        foreach (var value in prop.GetProperty("enum").EnumerateArray())
        {
            var key = value.GetString()!;
            cons.TryGetProperty(key, out var consequence).ShouldBeTrue($"{node}.{forkField} option '{key}' has no consequence line");
            consequence.GetString().ShouldNotBeNullOrWhiteSpace();
            labels.TryGetProperty(key, out var label).ShouldBeTrue($"{node}.{forkField} option '{key}' has no label");
            label.GetString().ShouldNotBeNullOrWhiteSpace();
        }
    }

    // riskLevel is a glanceable severity scale, not a behaviour fork — a segmented button group, not cards.
    [Fact]
    public void FlowDecision_riskLevel_is_a_segmented_severity_scale()
    {
        var prop = DecisionConfig.GetProperty("properties").GetProperty("riskLevel");

        prop.GetProperty("x-control").GetString().ShouldBe("segmented");
        prop.GetProperty("x-enumLabels").GetProperty("medium").GetString().ShouldBe("Medium");
    }

    // The decision inspector opens with a plain-language intent built from the question free-text.
    [Fact]
    public void FlowDecision_declares_a_question_intent()
    {
        DecisionConfig.GetProperty("x-intent").GetString().ShouldContain("{question}");
        DecisionConfig.GetProperty("x-intentPlaceholders").GetProperty("question").GetString().ShouldNotBeNullOrWhiteSpace();
    }
}
