using System.Text.Json;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Workflows.Planning.Planners;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The critic's projection (fail-closed per mode) + the schema/toggle pins — without a real model. GATE projects
/// approved/score/issues; IMPROVE projects a critique and fails closed on a blank one; the kill-switch defaults ON and
/// off only for an explicit disable.
/// </summary>
[Trait("Category", "Unit")]
public class LlmStructuredCriticTests
{
    [Fact]
    public void Project_gate_carries_approved_score_and_issues()
    {
        var json = Parse("""{ "approved": false, "score": 55, "issues": ["no rollback plan"], "rationale": "thin" }""");

        var verdict = LlmStructuredCritic.Project(ReviewMode.Gate, json);

        verdict.Failed.ShouldBeFalse();
        verdict.Mode.ShouldBe(ReviewMode.Gate);
        verdict.Approved.ShouldBeFalse();
        verdict.Score.ShouldBe(55);
        verdict.Issues.ShouldContain("no rollback plan");
        verdict.Rationale.ShouldBe("thin");
    }

    [Fact]
    public void Project_improve_carries_the_critique()
    {
        var json = Parse("""{ "critique": "add an integration test subtask", "issues": ["untested path"], "rationale": "missing coverage" }""");

        var verdict = LlmStructuredCritic.Project(ReviewMode.Improve, json);

        verdict.Failed.ShouldBeFalse();
        verdict.Mode.ShouldBe(ReviewMode.Improve);
        verdict.Critique.ShouldBe("add an integration test subtask");
        verdict.Issues.ShouldContain("untested path");
    }

    [Fact]
    public void Project_improve_fails_closed_on_a_blank_critique()
    {
        var json = Parse("""{ "critique": "", "rationale": "ok" }""");

        var verdict = LlmStructuredCritic.Project(ReviewMode.Improve, json);

        verdict.Failed.ShouldBeTrue("a blank critique is nothing to revise against → a failed review (the caller keeps the original)");
    }

    [Fact]
    public void Project_gate_degrades_a_blank_rationale_to_a_placeholder()
    {
        var verdict = LlmStructuredCritic.Project(ReviewMode.Gate, Parse("""{ "approved": true, "rationale": "" }"""));

        verdict.Approved.ShouldBeTrue();
        verdict.Rationale.ShouldNotBeNullOrWhiteSpace("a blank rationale degrades to a placeholder, never silent");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("garbage", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("  False  ", false)]
    public void CriticToggle_defaults_on_and_is_off_only_for_an_explicit_disable(string? raw, bool expected)
        => CriticToggle.IsEnabled(raw).ShouldBe(expected);

    [Fact]
    public void The_two_schemas_are_well_formed_objects()
    {
        CriticSchema.GateSchema.GetProperty("properties").TryGetProperty("approved", out _).ShouldBeTrue();
        CriticSchema.ImproveSchema.GetProperty("properties").TryGetProperty("critique", out _).ShouldBeTrue();
        CriticSchema.GateSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
        CriticSchema.ImproveSchema.GetProperty("additionalProperties").GetBoolean().ShouldBeFalse();
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
