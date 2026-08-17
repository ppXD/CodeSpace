using CodeSpace.Core.Services.Agents.Eval.Benchmark;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: Q2's qualification statistics + grant fold. Pins: the Wilson one-sided lower bound against known
/// values (the number a public claim may cite is the BOUND, never the point estimate); infra-unknown cells sit in
/// the denominator so a broken evaluator can only LOWER it; Sealed requires BOTH the bound and evaluator health
/// to clear — an infra-riddled round or an empty suite mints Shadow evidence, never a sealed claim.
/// </summary>
[Trait("Category", "Unit")]
public class QualificationRunnerTests
{
    [Theory]
    [InlineData(0, 0, 0.0)]        // empty suite ⇒ no claim
    [InlineData(0, 20, 0.0)]       // zero successes ⇒ bound 0
    [InlineData(20, 20, 0.881)]    // perfect 20/20 still bounds WELL below 1 — small n is honest
    [InlineData(15, 20, 0.567)]    // 75% point estimate bounds at ~57%
    [InlineData(150, 200, 0.696)]  // same rate, more n ⇒ tighter bound
    public void The_wilson_lower_bound_matches_known_values(int successes, int trials, double expected)
    {
        QualificationStatistics.WilsonLowerBound(successes, trials).ShouldBe(expected, tolerance: 0.001);
    }

    [Fact]
    public void Infra_unknown_cells_can_only_lower_the_bound()
    {
        var clean = QualificationStatistics.WilsonLowerBound(15, 20);
        var infraRidden = QualificationStatistics.WilsonLowerBound(15, 30);   // 10 infra cells joined the denominator

        infraRidden.ShouldBeLessThan(clean, "a broken evaluator must never inflate capability — its dead cells stay in the divisor");
    }

    [Fact]
    public void Sealed_requires_both_the_bound_and_evaluator_health()
    {
        var spec = new QualificationSpec { MinSolveRateLowerBound = 0.5, MinEvaluatorHealth = 0.9, ValidityDays = 30 };

        QualificationRunner.Grant(spec, Score(solved: 18, unsolved: 2, infra: 0), lowerBound: 0.7)
            .ShouldBe(PerformanceQualification.Sealed);

        QualificationRunner.Grant(spec, Score(solved: 18, unsolved: 2, infra: 0), lowerBound: 0.4)
            .ShouldBe(PerformanceQualification.Shadow, "below the bound bar — measured evidence, no sealed claim");

        QualificationRunner.Grant(spec, Score(solved: 18, unsolved: 0, infra: 4), lowerBound: 0.7)
            .ShouldBe(PerformanceQualification.Shadow, "an infra-riddled round proves nothing about the model — the instrument must be healthy to seal");

        QualificationRunner.Grant(spec, Score(solved: 0, unsolved: 0, infra: 0), lowerBound: 1.0)
            .ShouldBe(PerformanceQualification.Shadow, "an empty suite seals nothing");
    }

    private static CorpusCellScore Score(int solved, int unsolved, int infra) =>
        new() { Solved = solved, Unsolved = unsolved, Abstained = 0, InfraUnknown = infra };
}
