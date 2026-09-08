using CodeSpace.Core.Services.Review;
using CodeSpace.IntegrationTests.Workflows.Supervisor;
using CodeSpace.Messages.Review;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// Dispatch-only, report-only live calibration of the production rubric judge. The immutable owner-labelled corpus
/// catches self-praise, polished errors, unsupported citations, partial coverage, artifact prompt injection,
/// contradictions and keyword substitution. Quality remains a report while each observed-model stratum accumulates
/// enough history; corpus/version/identity instrumentation is asserted immediately.
/// </summary>
[Trait("Category", "RealModel")]
[Trait("Surface", "Engine")]
public sealed class RealModelJudgeCalibrationE2ETests
{
    private const string Provider = "Anthropic";

    [SkippableFact]
    public async Task The_real_rubric_judge_reports_versioned_false_positive_and_false_negative_intervals()
    {
        var baseUrl = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.BaseUrlEnvVar);
        var apiKey = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ApiKeyEnvVar);
        var model = RealModelLiveWire.Env(RealModelSupervisorDecisionFlowTests.ModelIdEnvVar);
        var present = new[] { baseUrl, apiKey, model }.Count(value => value is not null);

        if (present == 0) throw RealModelGate.ReportSkipped(Provider, "CODESPACE_LLM_* absent; rubric-judge calibration was not measured");
        present.ShouldBe(3, "partial live-model configuration must fail rather than silently skip");

        await RealModelGate.AssessLiveAsync(Provider, async () =>
        {
            var judge = new LlmRubricJudge(RealModelLiveWire.Registry(), RealModelLiveWire.Selector(model!, RealModelLiveWire.Credential(Provider, baseUrl!, apiKey!)));
            var report = await new RubricJudgeCalibration(judge).RunAsync(new RubricJudgeCalibrationRequest { TeamId = Guid.NewGuid(), JudgeModelRowId = Guid.NewGuid() }, CancellationToken.None);

            report.CorpusGeneration.ShouldBe(RubricJudgeCalibrationCorpus.Generation);
            report.CorpusDigest.ShouldBe(RubricJudgeCalibrationCorpus.Digest());
            report.Observations.Count.ShouldBe(RubricJudgeCalibrationCorpus.Cases.Count);
            report.Observations.ShouldAllBe(observation => observation.EvaluatorGeneration == LlmRubricJudge.EvaluatorGeneration);
            report.Observations.ShouldAllBe(observation => RubricJudgeCalibrationCorpus.Cases.Any(item => item.Id == observation.CaseId && item.Category == observation.Category));

            var sampleSize = report.Strata.Sum(stratum => stratum.SampleSize);
            var evaluated = report.Strata.Sum(stratum => stratum.EvaluatedSampleSize);
            var correct = report.Strata.Sum(stratum => stratum.TruePositives + stratum.TrueNegatives);
            var falsePositives = report.Strata.Sum(stratum => stratum.FalsePositives);
            var falseNegatives = report.Strata.Sum(stratum => stratum.FalseNegatives);
            sampleSize.ShouldBe(RubricJudgeCalibrationCorpus.Cases.Count);

            var intervals = string.Join("; ", report.Strata.Select(stratum =>
                $"n={stratum.SampleSize}, evaluated={stratum.EvaluatedSampleSize}, health={stratum.EvaluatorHealth:P1}, FP95={Format(stratum.FalsePositiveRate95)}, FN95={Format(stratum.FalseNegativeRate95)}, identityObserved={stratum.JudgeModel is not null}"));
            var note = $"corpus={report.CorpusGeneration}@{report.CorpusDigest[..12]}, correct={correct}/{sampleSize}, FP={falsePositives}, FN={falseNegatives}; {intervals}";
            Console.WriteLine($"[rubric-judge-calibration] {note}");

            var perfectRound = evaluated == sampleSize && falsePositives == 0 && falseNegatives == 0 && report.Strata.All(stratum => stratum.JudgeModel is not null);
            return (perfectRound ? RealModelOutcome.Drove : RealModelOutcome.CapabilityMiss, note);
        });
    }

    private static string Format(BinomialConfidenceInterval? interval) => interval is null ? "n/a" : $"[{interval.Lower:P1},{interval.Upper:P1}]";
}
