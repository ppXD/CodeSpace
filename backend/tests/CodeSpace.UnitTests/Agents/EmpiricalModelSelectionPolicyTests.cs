using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

[Trait("Category", "Unit")]
public sealed class EmpiricalModelSelectionPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Healthy_current_evidence_can_raise_or_lower_a_declared_prior()
    {
        var empirical = Candidate(ModelCapabilityTier.Basic, Observation(lowerBound: 0.8));
        var declared = Candidate(ModelCapabilityTier.Strong);

        EmpiricalModelSelectionPolicy.Select(new[] { empirical, declared }, Now)!.RowId.ShouldBe(empirical.RowId);

        empirical = empirical with { Observations = new[] { Observation(lowerBound: 0.4) } };
        EmpiricalModelSelectionPolicy.Select(new[] { empirical, declared }, Now)!.RowId.ShouldBe(declared.RowId);
    }

    [Fact]
    public void Strong_empirical_evidence_can_beat_the_highest_unmeasured_prior()
    {
        var measured = Candidate(ModelCapabilityTier.Unknown, Observation(lowerBound: 0.9));
        var unmeasuredFrontier = Candidate(ModelCapabilityTier.Frontier);

        var selected = EmpiricalModelSelectionPolicy.Select(new[] { unmeasuredFrontier, measured }, Now)!;

        selected.RowId.ShouldBe(measured.RowId);
        selected.Receipt.Source.ShouldBe(ModelSelectionSource.QualificationEvidence);
    }

    [Fact]
    public void Weak_or_unhealthy_evidence_falls_back_to_the_versioned_prior()
    {
        var frontier = Candidate(ModelCapabilityTier.Frontier, Observation(lowerBound: 0.99, sampleSize: 4));
        var strong = Candidate(ModelCapabilityTier.Strong, Observation(lowerBound: 0.99, evaluatorHealth: 0.89));

        var selected = EmpiricalModelSelectionPolicy.Select(new[] { strong, frontier }, Now)!;

        selected.RowId.ShouldBe(frontier.RowId);
        selected.Receipt.Source.ShouldBe(ModelSelectionSource.DeclaredPrior);
        selected.Receipt.QualificationReceiptId.ShouldBeNull();
    }

    [Fact]
    public void Only_the_latest_evaluation_generation_is_compared_and_age_decays_evidence()
    {
        var oldGeneration = Candidate(ModelCapabilityTier.Unknown, Observation(lowerBound: 0.99, suiteDigest: "sha256:old", effectiveFrom: Now.AddDays(-2), expiresAt: Now.AddDays(28)));
        var current = Candidate(ModelCapabilityTier.Unknown, Observation(lowerBound: 0.75, suiteDigest: "sha256:new", effectiveFrom: Now.AddHours(-1), expiresAt: Now.AddDays(29)));
        var staleInCurrent = Candidate(ModelCapabilityTier.Unknown, Observation(lowerBound: 0.9, suiteDigest: "sha256:new", effectiveFrom: Now.AddDays(-29), expiresAt: Now.AddDays(1)));

        var selected = EmpiricalModelSelectionPolicy.Select(new[] { oldGeneration, staleInCurrent, current }, Now)!;

        selected.RowId.ShouldBe(current.RowId);
        selected.Receipt.SuiteDigest.ShouldBe("sha256:new");
        var adjusted = selected.Receipt.AgeAdjustedScore.ShouldNotBeNull();
        adjusted.ShouldBeGreaterThan(0.7);
        adjusted.ShouldBeLessThanOrEqualTo(0.75);
    }

    private static EmpiricalModelCandidate Candidate(ModelCapabilityTier tier, params EmpiricalModelObservation[] observations) => new()
    {
        RowId = Guid.NewGuid(), ModelId = Guid.NewGuid().ToString("N"), IsDefault = false, DeclaredTier = tier,
        Observations = observations,
    };

    private static EmpiricalModelObservation Observation(double lowerBound, int sampleSize = 20, double evaluatorHealth = 1, string suiteDigest = "sha256:current", DateTimeOffset? effectiveFrom = null, DateTimeOffset? expiresAt = null) => new()
    {
        ReceiptId = Guid.NewGuid(), SuiteDigest = suiteDigest, EvidenceVersion = ModelQualificationEvidence.CurrentVersion,
        SampleSize = sampleSize, SolveRateLowerBound = lowerBound, EvaluatorHealth = evaluatorHealth,
        EffectiveFrom = effectiveFrom ?? Now.AddHours(-1), ExpiresAt = expiresAt ?? Now.AddDays(29),
    };
}
