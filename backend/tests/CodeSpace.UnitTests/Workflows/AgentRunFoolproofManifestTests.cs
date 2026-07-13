using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// agent.run (AgentCodeNode) renders its four behaviour-fork enums — autonomyLevel, mode, cwdMode,
/// outputReviewMode — as radioCards where EVERY option carries a plain-language consequence, so an author
/// understands the security / write / branch / review effect before choosing. Presentation-only x-* hints the
/// engine ignores: radioCards stores the SAME enum string (or int-as-string) the &lt;select&gt; did —
/// autonomyLevel/mode/cwdMode parse case-insensitively, outputReviewMode via a string-tolerant ReadInt — so the
/// config VALUE shape is unchanged and every stored workflow is unaffected. The copy was verified against the
/// node runtime (autonomy → sandbox posture + tool gate, mode → permission base + push opt-in, cwd → working
/// directory, output review → Gate re-grades to NeedsReview / Improve grants one revise round).
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunFoolproofManifestTests
{
    private static JsonElement Config => new AgentCodeNode().Manifest.ConfigSchema;

    public static IEnumerable<object[]> Forks()
    {
        yield return new object[] { "autonomyLevel" };
        yield return new object[] { "mode" };
        yield return new object[] { "cwdMode" };
        yield return new object[] { "outputReviewMode" };
    }

    [Theory]
    [MemberData(nameof(Forks))]
    public void Behaviour_fork_is_radiocards_and_explains_every_option(string forkField)
    {
        var prop = Config.GetProperty("properties").GetProperty(forkField);

        prop.GetProperty("x-control").GetString().ShouldBe("radioCards", $"agent.run.{forkField} is a behaviour fork — render it as stacked cards, not a bare dropdown");

        prop.TryGetProperty("x-optionConsequence", out var cons).ShouldBeTrue($"agent.run.{forkField} must declare x-optionConsequence so no option is a mystery");
        prop.TryGetProperty("x-enumLabels", out var labels).ShouldBeTrue($"agent.run.{forkField} must declare x-enumLabels");

        foreach (var value in prop.GetProperty("enum").EnumerateArray())
        {
            var key = value.ToString();
            cons.TryGetProperty(key, out var consequence).ShouldBeTrue($"agent.run.{forkField} option '{key}' has no consequence line");
            consequence.GetString().ShouldNotBeNullOrWhiteSpace();
            labels.TryGetProperty(key, out var label).ShouldBeTrue($"agent.run.{forkField} option '{key}' has no label");
            label.GetString().ShouldNotBeNullOrWhiteSpace();
        }
    }
}
