using CodeSpace.Messages.Contracts;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Q1 — the IMMUTABLE record of a measured qualification claim: which hidden suite (by digest), judged by which
/// verifier/model/runner bundle, over which launch cohort + mode + capability, granting which
/// <see cref="PerformanceQualification"/> tier, valid for which window. Public SOTA numbers and formal capability
/// claims must trace to a <see cref="PerformanceQualification.Sealed"/> receipt. The registry may REVOKE
/// forward-only (<see cref="RevokedAt"/>, a one-way flip changing future gating) — a receipt's claim about the
/// past is never rewritten, and the table's trigger enforces exactly that.
/// </summary>
public class QualificationReceipt : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>The operating mode this receipt qualifies (a <c>RunModeKeys</c> value).</summary>
    public string Mode { get; set; } = "";

    /// <summary>The capability this receipt qualifies (a <c>CapabilityKeys</c> value).</summary>
    public string CapabilityKey { get; set; } = "";

    /// <summary>The hidden suite's content digest — WHICH tasks were run, pinned so a later suite edit can never claim this receipt's number.</summary>
    public string SuiteDigest { get; set; } = "";

    /// <summary>The verifier + model + runner identities the qualification ran under (jsonb) — the claim is only as strong as the bundle that judged it.</summary>
    public string VerifierBundleJson { get; set; } = "{}";

    /// <summary>The launch cohort descriptor the qualification covers (jsonb — team/projection/tier/policy/mode-profile/security-profile).</summary>
    public string CohortJson { get; set; } = "{}";

    /// <summary>The performance tier this receipt grants — stored as text (wire-stable).</summary>
    public PerformanceQualification GrantedPerformance { get; set; }

    /// <summary>The measured numbers backing the grant (jsonb) — solve rate with bounds, cost/solve, latency, human-intervention rate. Null when the mint recorded none.</summary>
    public string? MetricsJson { get; set; }

    public DateTimeOffset EffectiveFrom { get; set; }

    /// <summary>Hard validity horizon — a claim does not outlive its window; re-qualification mints a NEW receipt.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Forward-only revocation stamp — set once, never cleared; changes FUTURE gating only.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }
}
