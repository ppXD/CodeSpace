/** The rendered form of a self-report/check disagreement — the chip's tone, its label, and the hover that explains it. */
export interface ContradictionChip {
  /** Reuses the review chip's two tones: `warn` for a claim the check refuted, `ok` for work the check vindicated. */
  tone: "ok" | "warn";
  text: string;
  title: string;
}

/**
 * The copy for an agent card's `contradiction` — the backend's `AgentContradiction` kinds, which are the SAME two
 * values on both lanes (a supervisor unit's folded compact and, since D4b, a single agent's own graded result):
 *
 * - `over_claim` — the agent reported success and the objective check failed.
 * - `under_claim` — the agent reported failure but the check PASSED, so the run is objectively fine and the platform
 *   kept the work instead of discarding it on the agent's word.
 *
 * Any other value (absent, null, or a kind this build doesn't know) renders NOTHING rather than a raw wire token —
 * an unrecognized classification must never leak a machine string into the room.
 */
export function contradictionChip(kind: string | null | undefined): ContradictionChip | null {
  switch (kind) {
    case "over_claim":
      return {
        tone: "warn",
        text: "⚠ claimed done · check failed",
        title: "The agent reported success, but the objective acceptance check failed — the run is Failed on the check's verdict, not on its own report.",
      };
    case "under_claim":
      return {
        tone: "ok",
        text: "✓ reported failure · check passed",
        title: "The agent reported failure, but the objective acceptance check PASSED — the work is objectively fine and was kept, so the run counts as delivered.",
      };
    default:
      return null;
  }
}
