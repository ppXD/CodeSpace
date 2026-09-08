using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Review;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Review;
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
    [Theory]
    [InlineData("producer-model", "PRODUCER-MODEL", ReviewModelIndependence.SameBackingModel, false)]
    [InlineData("producer-model", "judge-model", ReviewModelIndependence.DistinctBackingModel, true)]
    [InlineData("producer-model", null, ReviewModelIndependence.Unknown, false)]
    public async Task A_judge_is_calibrated_only_when_both_wire_identities_prove_distinct_backing_models(string producer, string? judgeModel, ReviewModelIndependence expected, bool calibrated)
    {
        var selector = new JudgeSelector();
        var judge = new LlmRubricJudge(new LLMClientRegistry([new JudgeClient(judgeModel)]), selector);
        var producerIdentity = new ReviewModelIdentity { ModelCredentialModelId = Guid.NewGuid(), ConfiguredModel = "producer-alias", ObservedModel = producer };

        var verdict = await judge.JudgeAsync(new RubricJudgeRequest { Rubric = Rubric("a"), Artifact = "complete", TeamId = Guid.NewGuid(), ProducerModel = producerIdentity }, CancellationToken.None);

        verdict.Failed.ShouldBeFalse();
        verdict.JudgeModel.ShouldBe(judgeModel);
        verdict.Independence.ShouldBe(expected);
        verdict.Calibrated.ShouldBe(calibrated);
        selector.ProducerModel.ShouldBe(producerIdentity, "automatic judge selection receives the same trusted producer identity used by the post-call comparison");
    }
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
    public async Task A_malformed_echo_keeps_the_wire_identity_in_that_models_health_stratum()
    {
        var producer = new ReviewModelIdentity { ObservedModel = "producer-wire" };
        var response = Json("""{ "criteria": [ { "id": "invented", "met": true, "evidence": "irrelevant" } ] }""");
        var judge = new LlmRubricJudge(new LLMClientRegistry([new JudgeClient("judge-wire", response)]), new JudgeSelector());

        var verdict = await judge.JudgeAsync(new RubricJudgeRequest { Rubric = Rubric("required"), Artifact = "artifact", TeamId = Guid.NewGuid(), ProducerModel = producer }, CancellationToken.None);

        verdict.Failed.ShouldBeTrue();
        verdict.JudgeModel.ShouldBe("judge-wire", "an invalid answer still came from an observed model and must reduce that model's evaluator health");
        verdict.Independence.ShouldBe(ReviewModelIndependence.DistinctBackingModel);
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

    // ─── IsInfraFailure (the shared failure classification every retry-shaped decision steers on — P0) ───

    [Theory]
    [InlineData("grade-error: judge parse failed", false, true)]   // the grader's own failure — infra regardless of work
    [InlineData("grade-error: judge parse failed", true, true)]
    [InlineData("clone-failed: authentication failed", false, true)]   // the grading CLONE failed (WorkspaceException arm — never grade-error-prefixed)
    [InlineData("no-rubric", false, true)]                      // half-authored spec — an agent cannot author the missing rubric
    [InlineData("no-schema", true, true)]
    [InlineData("no-branch-or-repo", true, true)]               // work EXISTS but publish failed — another agent pass changes nothing
    [InlineData("no-branch-or-repo", false, false)]             // NO work — the fix is to DO the work, which an agent pass CAN
    [InlineData("tests-timed-out", false, true)]                // P3.1: the grader's OWN wall-clock firing — an environment/workload fact, not a code defect
    [InlineData("tests-timed-out", true, true)]
    [InlineData("tests-failed-exit-1", true, false)]            // a real check verdict — the work is wrong; a retry can fix it
    [InlineData(null, true, false)]
    public void The_infra_classification_separates_unrunnable_checks_from_failed_work(string? detail, bool workPresent, bool expectInfra) =>
        AgentAcceptanceContract.IsInfraFailure(detail, workPresent)
            .ShouldBe(expectInfra, "retry-the-agent must never be spent on a failure class a retry cannot fix — the executor's revise loop, the decider prompt, and the evidence fold all steer on this one classification");

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

    private sealed class JudgeClient(string? observedModel, JsonElement? response = null) : ILLMClient, IStructuredLLMClient
    {
        public string Provider => "test";
        public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken) => Task.FromResult(new StructuredLLMCompletion
        {
            Model = observedModel ?? request.Model, ObservedModel = observedModel,
            Json = response ?? JsonSerializer.SerializeToElement(new { criteria = new[] { new { id = "a", met = true, evidence = "complete" } } }),
        });
    }

    private sealed class JudgeSelector : IModelPoolSelector
    {
        private readonly Guid _rowId = Guid.NewGuid();
        public ReviewModelIdentity? ProducerModel { get; private set; }
        public Task<Guid?> SelectReviewerRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, ReviewModelIdentity producerModel, CancellationToken cancellationToken) { ProducerModel = producerModel; return Task.FromResult<Guid?>(_rowId); }
        public Task<ModelPoolPick?> ResolveByRowIdAsync(Guid teamId, Guid modelCredentialModelId, CancellationToken cancellationToken) => Task.FromResult<ModelPoolPick?>(new() { ModelId = "judge-alias", Credential = new ResolvedModelCredential { Provider = "test" } });
        public Task<ModelPoolPick?> SelectAsync(Guid teamId, string provider, IReadOnlyList<string>? allowedModels, string? pinnedModel, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ModelDispatchRef?> ResolveDispatchAsync(Guid teamId, string modelName, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<PoolModelInfo>> ListPoolAsync(Guid teamId, IReadOnlyList<Guid>? allowedRowIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<Guid?> SelectBrainRowIdAsync(Guid teamId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => Task.FromResult<Guid?>(_rowId);
        public Task<Guid?> ResolvePinnedBrainRowIdAsync(Guid teamId, Guid modelCredentialModelId, IReadOnlyCollection<string> eligibleProviders, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> ResolveTeamDefaultProviderAsync(Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
