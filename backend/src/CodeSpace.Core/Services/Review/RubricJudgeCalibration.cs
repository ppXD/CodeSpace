using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents.Eval.Benchmark.Graders;
using CodeSpace.Messages.Review;

namespace CodeSpace.Core.Services.Review;

public sealed record RubricJudgeCalibrationRequest
{
    public required Guid TeamId { get; init; }
    public Guid? JudgeModelRowId { get; init; }
    public ReviewModelIdentity? ProducerModel { get; init; }
}

/// <summary>Runs the versioned owner-labelled corpus through the production rubric judge and returns an auditable per-identity confusion report.</summary>
public sealed class RubricJudgeCalibration(IRubricJudge judge) : IScopedDependency
{
    public async Task<JudgeCalibrationReport> RunAsync(RubricJudgeCalibrationRequest request, CancellationToken cancellationToken)
    {
        var observations = new List<JudgeCalibrationObservation>(RubricJudgeCalibrationCorpus.Cases.Count);

        foreach (var item in RubricJudgeCalibrationCorpus.Cases)
        {
            var verdict = await judge.JudgeAsync(new RubricJudgeRequest
            {
                Rubric = item.Rubric with { JudgeModelId = request.JudgeModelRowId },
                Artifact = item.Artifact,
                Goal = "Judge the supplied artifact only against the fixed rubric.",
                TeamId = request.TeamId,
                ProducerModel = request.ProducerModel,
            }, cancellationToken).ConfigureAwait(false);

            observations.Add(new JudgeCalibrationObservation
            {
                CaseId = item.Id,
                Category = item.Category,
                EvaluatorGeneration = LlmRubricJudge.EvaluatorGeneration,
                ExpectedPassed = item.ExpectedPassed,
                ActualPassed = verdict.Failed ? null : LlmJudgeGrader.Aggregate(item.Rubric, verdict).Passed,
                JudgeModel = verdict.JudgeModel,
                Independence = verdict.Independence,
                FailureDetail = verdict.Failed ? verdict.FailureDetail : null,
            });
        }

        return Analyze(RubricJudgeCalibrationCorpus.Generation, RubricJudgeCalibrationCorpus.Digest(), observations);
    }

    public static JudgeCalibrationReport Analyze(string corpusGeneration, string corpusDigest, IReadOnlyList<JudgeCalibrationObservation> observations)
    {
        var strata = observations.GroupBy(observation => (observation.EvaluatorGeneration, observation.JudgeModel), CalibrationKeyComparer.Instance)
            .Select(group => Stratum(group.Key.EvaluatorGeneration, group.Key.JudgeModel, group.ToList()))
            .OrderBy(stratum => stratum.EvaluatorGeneration, StringComparer.Ordinal).ThenBy(stratum => stratum.JudgeModel, StringComparer.Ordinal).ToList();
        return new JudgeCalibrationReport { CorpusGeneration = corpusGeneration, CorpusDigest = corpusDigest, Observations = observations, Strata = strata };
    }

    private static JudgeCalibrationStratum Stratum(string evaluatorGeneration, string? judgeModel, IReadOnlyList<JudgeCalibrationObservation> observations)
    {
        var evaluated = observations.Where(observation => observation.ActualPassed != null).ToList();
        var truePositives = evaluated.Count(observation => observation.ExpectedPassed && observation.ActualPassed == true);
        var trueNegatives = evaluated.Count(observation => !observation.ExpectedPassed && observation.ActualPassed == false);
        var falsePositives = evaluated.Count(observation => !observation.ExpectedPassed && observation.ActualPassed == true);
        var falseNegatives = evaluated.Count(observation => observation.ExpectedPassed && observation.ActualPassed == false);
        return new JudgeCalibrationStratum
        {
            EvaluatorGeneration = evaluatorGeneration,
            JudgeModel = judgeModel,
            SampleSize = observations.Count,
            EvaluatedSampleSize = evaluated.Count,
            IndependentSampleSize = evaluated.Count(observation => observation.Independence == ReviewModelIndependence.DistinctBackingModel),
            TruePositives = truePositives,
            TrueNegatives = trueNegatives,
            FalsePositives = falsePositives,
            FalseNegatives = falseNegatives,
            EvaluatorHealth = observations.Count == 0 ? 0 : (double)evaluated.Count / observations.Count,
            FalsePositiveRate95 = Wilson95(falsePositives, falsePositives + trueNegatives),
            FalseNegativeRate95 = Wilson95(falseNegatives, falseNegatives + truePositives),
        };
    }

    internal static BinomialConfidenceInterval? Wilson95(int events, int trials)
    {
        if (trials <= 0) return null;
        const double z = 1.959963984540054;
        var p = (double)events / trials;
        var z2 = z * z;
        var denominator = 1 + z2 / trials;
        var centre = (p + z2 / (2 * trials)) / denominator;
        var margin = z * Math.Sqrt(p * (1 - p) / trials + z2 / (4.0 * trials * trials)) / denominator;
        return new BinomialConfidenceInterval(Math.Max(0, centre - margin), Math.Min(1, centre + margin));
    }

    private sealed class CalibrationKeyComparer : IEqualityComparer<(string EvaluatorGeneration, string? JudgeModel)>
    {
        public static CalibrationKeyComparer Instance { get; } = new();
        public bool Equals((string EvaluatorGeneration, string? JudgeModel) left, (string EvaluatorGeneration, string? JudgeModel) right) =>
            string.Equals(left.EvaluatorGeneration, right.EvaluatorGeneration, StringComparison.Ordinal) && string.Equals(left.JudgeModel, right.JudgeModel, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string EvaluatorGeneration, string? JudgeModel) value) => HashCode.Combine(value.EvaluatorGeneration, value.JudgeModel?.ToUpperInvariant());
    }
}
