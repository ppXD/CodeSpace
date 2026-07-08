namespace CodeSpace.Messages.Enums;

/// <summary>
/// The launch composer's QUALITY dial as a REAL backend concept (P3.2) — Prototype/Delivery/Unattended mirror the
/// FE-only preset table (<c>frontend/src/lib/qualityPresets.ts</c>), which today is purely client-side sugar: it
/// only writes the underlying granular knobs (<c>OutputReviewMode</c>, <c>AcceptanceChecks</c>, ...) onto the
/// launch command, so a caller that skips the FE preset (a raw API call, or a different composer) can claim
/// "Delivery" with zero server-side consequence. This field + <c>TaskLaunchService</c>'s tier mandates make the
/// FLOOR real server-side instead of an unenforced convention. Default <see cref="Prototype"/> (self-report only,
/// byte-identical to before this field existed) when a caller sends no tier at all.
/// </summary>
public enum QualityTier
{
    /// <summary>No mandated review/acceptance — Succeeded means exit 0, the operator eyeballs the result themself. The default when a caller sends no tier at all.</summary>
    Prototype = 0,

    /// <summary>A human stays in the loop: MANDATES an executable acceptance floor on a supervisor-projected launch, and at least Gate-level output review — a weak/unverified result is flagged for a human, never silently shipped.</summary>
    Delivery,

    /// <summary>No human in the loop: the same acceptance mandate as <see cref="Delivery"/>, with the output-review floor raised to Improve — a flagged result buys one auto-revise before it can flag, so an unattended run has a chance to self-correct.</summary>
    Unattended,
}
