namespace CodeSpace.Messages.Review;

/// <summary>
/// How badly a <see cref="CriticIssue"/> undermines the artifact — the calibration axis that turns a binary
/// approve/flag into a proportionate verdict. The MODEL classifies each issue (what's wrong + how bad); the PLATFORM
/// derives the gating decision from a policy over the severities (default: halt on any <see cref="Blocker"/>). This
/// separation is the pyramid discipline — model judgment is graded, the halt/proceed choice is a deterministic policy —
/// and it is the fix for adversarial review that blocks on every nitpick: a <see cref="Minor"/> issue can no longer
/// carry the same halting power as a fatal one.
/// </summary>
public enum CriticSeverity
{
    /// <summary>A NITPICK — a style, wording, or non-load-bearing preference. Worth noting, never worth halting or a mandatory revision round.</summary>
    Minor = 0,

    /// <summary>A REAL problem worth fixing that does NOT make the artifact unfit for its goal — it still achieves the goal, imperfectly. Surfaced and fed to a revision, but does not by itself halt a gate.</summary>
    Major,

    /// <summary>A defect that makes the artifact UNFIT for its goal — it would produce wrong, broken, unsafe, or incomplete results, or fails a hard requirement. The one severity that HALTS a gate.</summary>
    Blocker,
}
