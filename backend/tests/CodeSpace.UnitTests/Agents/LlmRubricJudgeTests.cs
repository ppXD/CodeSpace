using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The rubric judge's PURE halves: the complete-echo projection (a verdict must cover the rubric — joined by id,
/// invented ids dropped, ANY missing criterion fails closed) and the prompt/schema contracts the deterministic test
/// judge and the live model both program against.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LlmRubricJudgeTests
{
    [Fact]
    public void A_complete_echo_projects_joined_by_id_in_rubric_order()
    {
        var verdict = LlmRubricJudge.Project(Rubric("a", "b"), Json("""{ "criteria": [ { "id": "b", "met": false, "evidence": "missing" }, { "id": "a", "met": true, "evidence": "found" } ] }"""));

        verdict.Failed.ShouldBeFalse();
        verdict.Criteria.Select(c => c.Id).ShouldBe(new[] { "a", "b" }, customMessage: "the verdict answers the RUBRIC's order, not the model's");
        verdict.Criteria[0].Met.ShouldBeTrue();
        verdict.Criteria[1].Evidence.ShouldBe("missing");
    }

    [Fact]
    public void A_missing_criterion_fails_the_whole_verdict_closed()
    {
        var verdict = LlmRubricJudge.Project(Rubric("a", "b"), Json("""{ "criteria": [ { "id": "a", "met": true, "evidence": "e" } ] }"""));

        verdict.Failed.ShouldBeTrue("a verdict that doesn't cover the rubric is not a verdict — fail-closed beats guessing the missing half");
        verdict.FailureDetail.ShouldContain("[b]");
    }

    [Fact]
    public void Invented_ids_are_dropped_and_duplicates_keep_the_first()
    {
        var verdict = LlmRubricJudge.Project(Rubric("a"), Json("""{ "criteria": [ { "id": "a", "met": true, "evidence": "first" }, { "id": "a", "met": false, "evidence": "second" }, { "id": "ghost", "met": true, "evidence": "?" } ] }"""));

        verdict.Failed.ShouldBeFalse();
        verdict.Criteria.Count.ShouldBe(1, "the verdict answers the contract, nothing else");
        verdict.Criteria[0].Evidence.ShouldBe("first");
    }

    [Fact]
    public void An_empty_echo_fails_closed() =>
        LlmRubricJudge.Project(Rubric("a"), Json("""{ "criteria": [] }""")).Failed.ShouldBeTrue();

    [Fact]
    public void The_prompt_renders_each_criterion_as_a_bracketed_line_the_fakes_and_the_model_parse()
    {
        var prompt = LlmRubricJudge.BuildUserPromptForTest(Rubric("cites", "risks"), "=== report.md ===\nbody", "research the market");

        prompt.ShouldContain("- [cites] requirement cites", customMessage: "the `- [id] requirement` line format is the deterministic judge fake's parse contract");
        prompt.ShouldContain("- [risks] requirement risks");
        prompt.ShouldContain("Goal the deliverable should serve:");
        prompt.ShouldContain("=== report.md ===");
        prompt.ShouldContain("met=true ONLY when", customMessage: "binary verdicts with evidence — never a Likert scale");
    }

    [Fact]
    public void The_verdict_schema_requires_id_met_evidence_per_criterion()
    {
        var items = LlmRubricJudge.RubricVerdictSchema.GetProperty("properties").GetProperty("criteria").GetProperty("items");

        items.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .ShouldBe(new[] { "id", "met", "evidence" }, customMessage: "evidence is REQUIRED — an unevidenced verdict is not auditable");
    }

    // ─── ValidateAuthored (the shared authoring rule the node applies fail-loud) ───

    [Fact]
    public void A_judge_spec_without_a_rubric_is_incomplete() =>
        AgentAcceptanceContract.ValidateAuthored(Spec(Messages.Agents.Benchmark.BenchmarkGradingKind.LlmJudge))
            .ShouldNotBeNull("a judge with no rubric can never grade — fail-loud at authoring, not at the billed run");

    [Fact]
    public void A_schema_spec_without_a_schema_is_incomplete() =>
        AgentAcceptanceContract.ValidateAuthored(Spec(Messages.Agents.Benchmark.BenchmarkGradingKind.ArtifactSchema))
            .ShouldNotBeNull();

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-0.2)]
    public void An_out_of_range_threshold_is_rejected(double threshold) =>
        AgentAcceptanceContract.ValidateAuthored(Spec(Messages.Agents.Benchmark.BenchmarkGradingKind.LlmJudge) with
        {
            Rubric = Rubric("a") with { Threshold = threshold },
        }).ShouldNotBeNull();

    [Fact]
    public void Duplicate_or_blank_criteria_are_rejected()
    {
        var dup = Rubric("a") with { Criteria = new[] { Criterion("a"), Criterion("a") } };
        AgentAcceptanceContract.ValidateAuthored(Spec(Messages.Agents.Benchmark.BenchmarkGradingKind.LlmJudge) with { Rubric = dup }).ShouldNotBeNull();

        var blank = Rubric("a") with { Criteria = new[] { Criterion("") } };
        AgentAcceptanceContract.ValidateAuthored(Spec(Messages.Agents.Benchmark.BenchmarkGradingKind.LlmJudge) with { Rubric = blank }).ShouldNotBeNull();
    }

    [Fact]
    public void Complete_specs_of_every_kind_validate()
    {
        AgentAcceptanceContract.ValidateAuthored(Spec(null)).ShouldBeNull("a bare TestsPass argv is the S5 shape — unchanged");
        AgentAcceptanceContract.ValidateAuthored(Spec(Messages.Agents.Benchmark.BenchmarkGradingKind.CitationsResolve)).ShouldBeNull("citations needs only paths");
        AgentAcceptanceContract.ValidateAuthored(Spec(Messages.Agents.Benchmark.BenchmarkGradingKind.LlmJudge) with { Rubric = Rubric("a") }).ShouldBeNull();
        AgentAcceptanceContract.ValidateAuthored(Spec(Messages.Agents.Benchmark.BenchmarkGradingKind.ArtifactSchema) with { Schema = Json("""{ "type": "object" }""") }).ShouldBeNull();
    }

    // ─── fixtures ────────────────────────────────────────────────────────────

    private static AcceptanceRubric Rubric(params string[] ids) => new() { Criteria = ids.Select(Criterion).ToList() };

    private static AcceptanceRubricCriterion Criterion(string id) => new() { Id = id, Requirement = string.IsNullOrEmpty(id) ? "" : $"requirement {id}" };

    private static SupervisorAcceptanceSpec Spec(Messages.Agents.Benchmark.BenchmarkGradingKind? kind) => new() { Command = new[] { "report.md" }, Kind = kind };

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}
