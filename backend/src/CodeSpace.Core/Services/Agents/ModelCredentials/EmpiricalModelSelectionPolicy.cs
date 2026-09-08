using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Agents.ModelCredentials;

/// <summary>One immutable qualification observation available to the launch-time model policy.</summary>
internal sealed record EmpiricalModelObservation
{
    public required Guid ReceiptId { get; init; }
    public required string SuiteDigest { get; init; }
    public required string EvidenceVersion { get; init; }
    public required int SampleSize { get; init; }
    public required double SolveRateLowerBound { get; init; }
    public required double EvaluatorHealth { get; init; }
    public required DateTimeOffset EffectiveFrom { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>One active pool row and its compatible qualification history.</summary>
internal sealed record EmpiricalModelCandidate
{
    public required Guid RowId { get; init; }
    public required string ModelId { get; init; }
    public required bool IsDefault { get; init; }
    public ModelCapabilityTier? ProbedTier { get; init; }
    public ModelCapabilityTier? DeclaredTier { get; init; }
    public IReadOnlyList<EmpiricalModelObservation> Observations { get; init; } = [];
}

internal sealed record EmpiricalModelSelectionResult(Guid RowId, ModelSelectionReceipt Receipt);

/// <summary>
/// P16-B's versioned, capability-generic model policy. The newest suite/evidence generation is the only empirical
/// generation compared. A healthy, sufficiently-sized Wilson lower bound may raise or lower the prior; old evidence
/// loses weight continuously as it approaches expiry. Weak evidence falls back to the model's objective-probe/declared
/// tier. No provider, model, task, or repository identity participates.
/// </summary>
internal static class EmpiricalModelSelectionPolicy
{
    public const int MinSampleSize = 10;
    public const double MinEvaluatorHealth = 0.9;
    private const double PriorStep = 0.2;

    public static EmpiricalModelSelectionResult? Select(IReadOnlyList<EmpiricalModelCandidate> candidates, DateTimeOffset now, string mode = "", string capabilityKey = "")
    {
        if (candidates.Count == 0) return null;

        var currentGeneration = candidates.SelectMany(candidate => candidate.Observations)
            .Where(observation => observation.EffectiveFrom <= now && observation.ExpiresAt > now)
            .OrderByDescending(observation => observation.EffectiveFrom)
            .ThenByDescending(observation => observation.ReceiptId)
            .Select(observation => new EvidenceGeneration(observation.SuiteDigest, observation.EvidenceVersion))
            .FirstOrDefault();

        var ranked = candidates.Select(candidate => Score(candidate, currentGeneration, now, mode, capabilityKey))
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.EvidenceSampleSize)
            .ThenByDescending(candidate => candidate.Candidate.IsDefault)
            .ThenBy(candidate => candidate.Candidate.ModelId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Candidate.RowId)
            .First();

        return new EmpiricalModelSelectionResult(ranked.Candidate.RowId, ranked.Receipt);
    }

    private static ScoredCandidate Score(EmpiricalModelCandidate candidate, EvidenceGeneration? generation, DateTimeOffset now, string mode, string capabilityKey)
    {
        var prior = Prior(candidate);
        var evidence = candidate.Observations
            .Where(observation => generation is not null && observation.SuiteDigest == generation.SuiteDigest && observation.EvidenceVersion == generation.EvidenceVersion && observation.EffectiveFrom <= now && observation.ExpiresAt > now)
            .OrderByDescending(observation => observation.EffectiveFrom)
            .ThenByDescending(observation => observation.ReceiptId)
            .FirstOrDefault();

        if (evidence is not null && evidence.SampleSize >= MinSampleSize && evidence.EvaluatorHealth >= MinEvaluatorHealth)
        {
            var lifetime = evidence.ExpiresAt - evidence.EffectiveFrom;
            var remaining = evidence.ExpiresAt - now;
            var freshness = lifetime <= TimeSpan.Zero ? 0 : Math.Clamp(remaining.TotalSeconds / lifetime.TotalSeconds, 0, 1);
            // Evidence loses influence with age and converges smoothly to the prior. Multiplying by freshness would
            // collapse toward zero and then jump back up to the prior at expiry, creating a perverse cliff.
            var adjusted = prior + freshness * (evidence.SolveRateLowerBound - prior);
            return new ScoredCandidate(candidate, adjusted, evidence.SampleSize, new ModelSelectionReceipt
            {
                Source = ModelSelectionSource.QualificationEvidence, ModelCredentialModelId = candidate.RowId, Mode = mode, CapabilityKey = capabilityKey,
                QualificationReceiptId = evidence.ReceiptId, SuiteDigest = evidence.SuiteDigest, EvidenceVersion = evidence.EvidenceVersion,
                SolveRateLowerBound = evidence.SolveRateLowerBound, AgeAdjustedScore = adjusted, SampleSize = evidence.SampleSize,
                EvaluatorHealth = evidence.EvaluatorHealth, EvidenceEffectiveFrom = evidence.EffectiveFrom, EvidenceExpiresAt = evidence.ExpiresAt,
            });
        }

        return new ScoredCandidate(candidate, prior, 0, new ModelSelectionReceipt
        {
            Source = ModelSelectionSource.DeclaredPrior, ModelCredentialModelId = candidate.RowId, Mode = mode, CapabilityKey = capabilityKey,
        });
    }

    private static double Prior(EmpiricalModelCandidate candidate)
    {
        var tier = AgentPlaneModelRanking.Effective(candidate.ProbedTier, candidate.DeclaredTier);
        // Priors rank an unmeasured pool without claiming certainty. Keeping the strongest prior below one leaves
        // headroom for strong empirical evidence to beat it; otherwise Frontier=1 would be mathematically unbeatable.
        // This mapping is part of the receipt's policy version and must change with that version if recalibrated.
        return ((int)tier + 1) * PriorStep;
    }

    private sealed record EvidenceGeneration(string SuiteDigest, string EvidenceVersion);
    private sealed record ScoredCandidate(EmpiricalModelCandidate Candidate, double Score, int EvidenceSampleSize, ModelSelectionReceipt Receipt);
}
