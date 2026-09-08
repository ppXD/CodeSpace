using CodeSpace.Core.Services.Review;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class JudgeCalibrationTests
{
    [Fact]
    public async Task The_runner_executes_every_fixed_case_through_the_rubric_judge_and_keeps_identity_evidence()
    {
        var judge = new CorpusJudge();
        var rowId = Guid.NewGuid();
        var producer = new ReviewModelIdentity { ObservedModel = "producer-wire" };

        var report = await new RubricJudgeCalibration(judge).RunAsync(new RubricJudgeCalibrationRequest { TeamId = Guid.NewGuid(), JudgeModelRowId = rowId, ProducerModel = producer }, CancellationToken.None);

        judge.Calls.ShouldBe(RubricJudgeCalibrationCorpus.Cases.Count);
        judge.Requests.ShouldAllBe(request => request.Rubric.JudgeModelId == rowId && request.ProducerModel == producer);
        report.Observations.Count.ShouldBe(RubricJudgeCalibrationCorpus.Cases.Count);
        var stratum = report.Strata.ShouldHaveSingleItem();
        stratum.TruePositives.ShouldBe(4);
        stratum.TrueNegatives.ShouldBe(7);
        stratum.FalsePositives.ShouldBe(0);
        stratum.FalseNegatives.ShouldBe(0);
        stratum.IndependentSampleSize.ShouldBe(11);
    }

    [Fact]
    public void The_versioned_corpus_contains_every_required_adversarial_semantic_and_has_a_stable_digest()
    {
        RubricJudgeCalibrationCorpus.Cases.Select(item => item.Category).Distinct().ShouldBe([
            JudgeCalibrationCategory.SoundControl,
            JudgeCalibrationCategory.SelfPraiseWithoutEvidence,
            JudgeCalibrationCategory.PolishedWrongAnswer,
            JudgeCalibrationCategory.UnsupportedCitation,
            JudgeCalibrationCategory.PartialCriterionCoverage,
            JudgeCalibrationCategory.ArtifactPromptInjection,
            JudgeCalibrationCategory.ContradictoryEvidence,
            JudgeCalibrationCategory.KeywordSubstitution,
        ]);
        RubricJudgeCalibrationCorpus.Cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count().ShouldBe(RubricJudgeCalibrationCorpus.Cases.Count);
        RubricJudgeCalibrationCorpus.Digest().ShouldBe("7944329a39547f3d581a4f90db6f3f944a91c76bdd2a21ddbd76c7c6a5bd448c");
        RubricJudgeCalibrationCorpus.Digest().Length.ShouldBe(64);
    }

    [Fact]
    public void Reports_are_stratified_by_wire_identity_and_evaluator_generation_with_failures_in_health()
    {
        var observations = new[]
        {
            Observation("good", true, true, "judge-a", ReviewModelIndependence.DistinctBackingModel),
            Observation("bad-1", false, true, "JUDGE-A", ReviewModelIndependence.DistinctBackingModel),
            Observation("bad-2", false, false, "judge-a", ReviewModelIndependence.SameBackingModel),
            Observation("failed", false, null, "judge-a", ReviewModelIndependence.Unknown),
            Observation("other-generation", false, false, "judge-a", ReviewModelIndependence.DistinctBackingModel) with { EvaluatorGeneration = "judge/v2" },
            Observation("unknown-wire", false, false, null, ReviewModelIndependence.Unknown),
        };

        var report = RubricJudgeCalibration.Analyze("corpus/v1", "digest", observations);
        var primary = report.Strata.Single(item => item is { EvaluatorGeneration: "judge/v1", JudgeModel: "judge-a" });

        primary.SampleSize.ShouldBe(4);
        primary.EvaluatedSampleSize.ShouldBe(3);
        primary.IndependentSampleSize.ShouldBe(2, "same-model and unknown observations can be monitored but cannot become independent qualification evidence");
        primary.TruePositives.ShouldBe(1);
        primary.TrueNegatives.ShouldBe(1);
        primary.FalsePositives.ShouldBe(1);
        primary.FalseNegatives.ShouldBe(0);
        primary.EvaluatorHealth.ShouldBe(0.75);
        primary.FalsePositiveRate95.ShouldNotBeNull().Lower.ShouldBeLessThan(0.5);
        primary.FalsePositiveRate95.Upper.ShouldBeGreaterThan(0.5);
        primary.FalseNegativeRate95.ShouldNotBeNull().Upper.ShouldBeGreaterThan(0);
        report.Strata.Count.ShouldBe(3, "a different generation and an unknown wire identity are never pooled into the primary stratum");
    }

    [Theory]
    [InlineData(0, 10, 0.0, 0.2775)]
    [InlineData(10, 10, 0.7225, 1.0)]
    [InlineData(5, 10, 0.2366, 0.7634)]
    public void Wilson_intervals_are_two_sided_95_percent_and_bounded(int events, int trials, double lower, double upper)
    {
        var interval = RubricJudgeCalibration.Wilson95(events, trials).ShouldNotBeNull();
        interval.Lower.ShouldBe(lower, 0.0002);
        interval.Upper.ShouldBe(upper, 0.0002);
    }

    [Fact]
    public void A_missing_positive_or_negative_class_has_no_fabricated_rate()
    {
        var onlyPositive = RubricJudgeCalibration.Analyze("c", "d", [Observation("positive", true, true, "judge", ReviewModelIndependence.DistinctBackingModel)]).Strata.Single();

        onlyPositive.FalsePositiveRate95.ShouldBeNull();
        onlyPositive.FalseNegativeRate95.ShouldNotBeNull();
        RubricJudgeCalibration.Wilson95(0, 0).ShouldBeNull();
    }

    private static JudgeCalibrationObservation Observation(string caseId, bool expected, bool? actual, string? model, ReviewModelIndependence independence) => new()
    {
        CaseId = caseId,
        Category = JudgeCalibrationCategory.SoundControl,
        EvaluatorGeneration = "judge/v1",
        ExpectedPassed = expected,
        ActualPassed = actual,
        JudgeModel = model,
        Independence = independence,
        FailureDetail = actual is null ? "judge unavailable" : null,
    };

    private sealed class CorpusJudge : IRubricJudge
    {
        public int Calls => Requests.Count;
        public List<RubricJudgeRequest> Requests { get; } = [];

        public Task<RubricJudgeVerdict> JudgeAsync(RubricJudgeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var expected = RubricJudgeCalibrationCorpus.Cases.Single(item => item.Artifact == request.Artifact).ExpectedPassed;
            return Task.FromResult(new RubricJudgeVerdict
            {
                Criteria = request.Rubric.Criteria.Select(criterion => new RubricCriterionVerdict { Id = criterion.Id, Met = expected, Evidence = expected ? "present" : "missing" }).ToList(),
                JudgeModel = "judge-wire",
                Independence = ReviewModelIndependence.DistinctBackingModel,
            });
        }

        public Task<RubricJudgeVerdict> JudgeAsync(Messages.Agents.AcceptanceRubric rubric, string artifact, string? goal, Guid teamId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
