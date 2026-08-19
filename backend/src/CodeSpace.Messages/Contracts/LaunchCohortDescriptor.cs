namespace CodeSpace.Messages.Contracts;

/// <summary>
/// Q5 — layer 1 of the qualification identity split: WHICH cohort a run (or a qualification round) belongs to,
/// composed ONLY of facts knowable at launch/staging time. A qualification receipt carries this as its
/// <c>cohort_jsonb</c> so a granted standing names exactly who it covers; a claim reader renders it as the
/// audit trail. Every property is required — a partial cohort is no cohort (legacy ad-hoc json parses to null,
/// never to a half-filled descriptor).
/// </summary>
public sealed record LaunchCohortDescriptor
{
    /// <summary>The tier a hidden qualification round runs under — owner-held suite, internal harness, no production traffic.</summary>
    public const string InternalQualificationTier = "internal-qualification";

    public required Guid TeamId { get; init; }

    /// <summary>The operating mode (a <c>RunModeKeys</c> value) — the cohort's conformance story.</summary>
    public required string Mode { get; init; }

    /// <summary>The cohort's tier (e.g. <see cref="InternalQualificationTier"/>) — what kind of traffic the standing covers.</summary>
    public required string Tier { get; init; }

    /// <summary>The completion-policy version the cohort's runs are assessed under — a standing earned under one protocol revision never silently covers another.</summary>
    public required int CompletionPolicyVersion { get; init; }

    /// <summary>The tasks lane's projection kind when one applies — null for a qualification round (no workflow run) and for the authored lane.</summary>
    public string? ProjectionKind { get; init; }
}
