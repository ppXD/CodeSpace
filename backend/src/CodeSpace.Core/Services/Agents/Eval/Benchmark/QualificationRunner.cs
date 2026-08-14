using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Contracts;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CodeSpace.Core.Services.Agents.Eval.Benchmark;

/// <summary>The committed qualification bar one mint runs against — caller-supplied policy (an ops decision per qualification round), never an env toggle.</summary>
public sealed record QualificationSpec
{
    /// <summary>The one-sided 95% LOWER confidence bound the suite solve-rate must clear for a Sealed grant — the number a public claim may cite is the bound, never the point estimate.</summary>
    public required double MinSolveRateLowerBound { get; init; }

    /// <summary>The minimum share of cells whose verdict is a REAL capability verdict — an infra-riddled round must never seal (a broken evaluator proves nothing about the model).</summary>
    public required double MinEvaluatorHealth { get; init; }

    /// <summary>How long the minted claim stands before re-qualification is owed.</summary>
    public required int ValidityDays { get; init; }
}

/// <summary>One qualification round's outcome: the frozen-denominator score, the one-sided lower bound, the tier granted, and the immutable receipt minted for it.</summary>
public sealed record QualificationOutcome(CorpusCellScore Score, double SolveRateLowerBound, PerformanceQualification Granted, Guid ReceiptId, string SuiteDigest);

/// <summary>The sealed-suite source seam — production reads THE conventional owner-held location; a test injects its own directory. Never an env toggle: pointing production elsewhere is a code change.</summary>
public interface IHiddenSuiteSource
{
    HiddenSuite? Load();
}

/// <summary>Default source: <see cref="HiddenSuiteLoader.DefaultSuiteDirectory"/>, the one conventional path.</summary>
public sealed class DefaultHiddenSuiteSource : IHiddenSuiteSource, DependencyInjection.ISingletonDependency
{
    public HiddenSuite? Load() => HiddenSuiteLoader.LoadFromDefaultLocation();
}

public interface IQualificationRunner
{
    /// <summary>
    /// Run the HIDDEN suite for (mode, capability) under <paramref name="selection"/> and mint the immutable
    /// qualification receipt: load the owner-held sealed suite (absent ⇒ throws — a qualification round without a
    /// suite is a misconfiguration, never a silent pass), run every cell @1 over the frozen denominator, compute
    /// the one-sided lower confidence bound with infra-unknown cells counted AGAINST the rate (a broken evaluator
    /// can never inflate capability), grant <see cref="PerformanceQualification.Sealed"/> only when BOTH the bound
    /// and the evaluator-health bar clear — anything less mints a <see cref="PerformanceQualification.Shadow"/>
    /// receipt (measured evidence, no sealed claim).
    /// </summary>
    Task<QualificationOutcome> QualifyAsync(string mode, string capabilityKey, QualificationSpec spec, Guid teamId, BenchmarkAgentSelection selection, CancellationToken cancellationToken);
}

/// <summary>Q2: the executable half the loader was waiting for — suite → corpus runner → frozen-denominator statistics → immutable receipt.</summary>
public sealed class QualificationRunner : IQualificationRunner, DependencyInjection.IScopedDependency
{
    private readonly IHiddenSuiteSource _suite;
    private readonly ICorpusBenchmarkRunner _corpus;
    private readonly IQualificationReceiptStore _receipts;
    private readonly ILogger<QualificationRunner> _logger;

    public QualificationRunner(IHiddenSuiteSource suite, ICorpusBenchmarkRunner corpus, IQualificationReceiptStore receipts, ILogger<QualificationRunner> logger)
    {
        _suite = suite;
        _corpus = corpus;
        _receipts = receipts;
        _logger = logger;
    }

    public async Task<QualificationOutcome> QualifyAsync(string mode, string capabilityKey, QualificationSpec spec, Guid teamId, BenchmarkAgentSelection selection, CancellationToken cancellationToken)
    {
        var suite = _suite.Load()
            ?? throw new InvalidOperationException($"No hidden suite at '{HiddenSuiteLoader.DefaultSuiteDirectory}' — a qualification round without the owner-held sealed suite is a misconfiguration, never a silent pass");

        var run = await _corpus.RunAsync(suite.Tasks, teamId, selection, cancellationToken).ConfigureAwait(false);

        var score = EvalSuite.Score(run.Cells ?? Array.Empty<CorpusCellOutcome>());
        var lowerBound = QualificationStatistics.WilsonLowerBound(score.Solved, score.Total);
        var granted = Grant(spec, score, lowerBound);

        var receipt = new QualificationReceipt
        {
            Id = Guid.NewGuid(),
            Mode = mode,
            CapabilityKey = capabilityKey,
            SuiteDigest = suite.SuiteContentHash,
            VerifierBundleJson = JsonSerializer.Serialize(new { harness = selection.Harness, model = selection.Model, modelCredentialId = selection.ModelCredentialId }, Agents.AgentJson.Options),
            CohortJson = JsonSerializer.Serialize(new { teamId, tier = "internal-qualification" }, Agents.AgentJson.Options),
            GrantedPerformance = granted,
            MetricsJson = JsonSerializer.Serialize(new
            {
                solved = score.Solved, unsolved = score.Unsolved, abstained = score.Abstained, infraUnknown = score.InfraUnknown,
                total = score.Total, solveRate = score.SolveRateOverSuite, solveRateLowerBound = lowerBound, evaluatorHealth = score.EvaluatorHealth,
            }, Agents.AgentJson.Options),
            EffectiveFrom = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(spec.ValidityDays),
        };

        await _receipts.AppendAsync(receipt, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Qualification round for ({Mode}, {Capability}): {Granted} — solved {Solved}/{Total}, lower bound {Bound:F3}, evaluator health {Health:F3}, suite {Digest}",
            mode, capabilityKey, granted, score.Solved, score.Total, lowerBound, score.EvaluatorHealth, suite.SuiteContentHash);

        return new QualificationOutcome(score, lowerBound, granted, receipt.Id, suite.SuiteContentHash);
    }

    /// <summary>The grant fold: Sealed only when the LOWER BOUND clears the bar AND the evaluator itself was healthy — an infra-riddled round or a thin suite mints Shadow evidence, never a sealed claim.</summary>
    internal static PerformanceQualification Grant(QualificationSpec spec, CorpusCellScore score, double lowerBound) =>
        score.Total > 0 && lowerBound >= spec.MinSolveRateLowerBound && score.EvaluatorHealth >= spec.MinEvaluatorHealth
            ? PerformanceQualification.Sealed
            : PerformanceQualification.Shadow;
}

/// <summary>The qualification statistics — pure, pinned by test.</summary>
public static class QualificationStatistics
{
    /// <summary>The one-sided 95% z (the only confidence level qualification speaks — a committed constant, never a knob).</summary>
    public const double OneSided95Z = 1.6448536269514722;

    /// <summary>
    /// The Wilson score interval's LOWER bound, one-sided at 95% — the conservative number a public claim may
    /// cite. Infra-unknown and abstained cells sit in <paramref name="trials"/> without contributing successes,
    /// so a broken evaluator or a thin round can only LOWER the bound. 0 for an empty suite.
    /// </summary>
    public static double WilsonLowerBound(int successes, int trials, double z = OneSided95Z)
    {
        if (trials <= 0) return 0;

        var p = (double)successes / trials;
        var z2 = z * z;
        var denominator = 1 + z2 / trials;
        var centre = p + z2 / (2 * trials);
        var margin = z * Math.Sqrt(p * (1 - p) / trials + z2 / (4.0 * trials * trials));

        return Math.Max(0, (centre - margin) / denominator);
    }
}
