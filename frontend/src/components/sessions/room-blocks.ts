import type { DiagnosticBlock, FinalAnswerBlock, RoomBlock } from "@/api/sessions";

/**
 * The RESULT card's heading. A degraded card is never the green "Result" — and when the backend authored an account of
 * WHY ("Checks failed", for a run whose acceptance check failed behind its own confident closing line) that account IS
 * the heading, verbatim. The FE owns no outcome vocabulary of its own: "Stopped" is only the fallback for a degrade
 * whose reason the card's own TEXT already carries (a give-up / forced stop).
 */
export function finalAnswerHeading(answer: Pick<FinalAnswerBlock, "degraded" | "degradedReason">): string {
  if (answer.degraded !== true) return "Result";
  return answer.degradedReason?.trim() || "Stopped";
}

/**
 * Failure-first ordering for a turn's blocks. On a failed turn the backend emits a `diagnostic` block — the humanized
 * cause plus its typed remediation — in the result family, which the Room otherwise renders at the very BOTTOM, under
 * the whole narrative. Pull it out so the view can render it right beneath the execution rail: the reader sees WHY it
 * failed and how to recover before reading the story. `rest` keeps every other block in its original order, so nothing
 * else moves. A turn with no diagnostic (success / still running) returns `{ hoisted: null, rest: [...blocks] }`, so the
 * happy-path ordering is byte-identical to before.
 */
export function partitionForFailureHoist(blocks: readonly RoomBlock[]): { hoisted: DiagnosticBlock | null; rest: RoomBlock[] } {
  const hoisted = (blocks.find((b) => b.type === "diagnostic") as DiagnosticBlock | undefined) ?? null;
  return { hoisted, rest: hoisted ? blocks.filter((b) => b.id !== hoisted.id) : [...blocks] };
}
