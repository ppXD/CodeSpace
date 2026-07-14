import { useEffect, useRef, useState } from "react";

import type { NodeKind, NodeStatus } from "@/api/workflows";

/** A one-shot beat the CSS plays once via `.wf-rf-node[data-beat]` — ignite (trigger fired), settle (terminal reached), verdict (routing node decided). */
export type StatusBeat = "ignite" | "settle" | "verdict";

/** How long the `data-beat` attribute stays set — matches the CSS keyframe window so the one play completes, then the attribute clears. */
const BEAT_MS = 1200;

/** The routing typeKeys whose settle reads as a "verdict" (a branch/route was decided), not a plain terminal. */
const ROUTING_TYPES = new Set(["logic.if", "logic.merge", "flow.iterate", "flow.decision", "flow.try"]);

/**
 * Fires a one-shot status beat when a node TRANSITIONS into a beat-worthy Success while mounted — a trigger igniting,
 * a terminal settling, a routing node rendering its verdict. Returns the beat name for ~1.2s (drives the card's
 * `data-beat` attribute, which the A1 CSS plays once) then clears it.
 *
 * The no-replay-on-mount guard is load-bearing: opening a run that is ALREADY at a terminal state must be silent, so
 * the very first render never counts as a transition — only a status change observed after mount fires a beat. The
 * previous status must have been non-terminal/absent (Pending / Running / Suspended / undefined) and the new status
 * Success; anything else (or reduced-motion) returns undefined and paints no beat.
 */
export function useStatusBeat(status: NodeStatus | undefined, kind: NodeKind, typeKey: string): StatusBeat | undefined {
  const prevRef = useRef<NodeStatus | undefined>(status);
  const [beat, setBeat] = useState<StatusBeat | undefined>(undefined);

  useEffect(() => {
    const prev = prevRef.current;
    prevRef.current = status;

    if (prev === status) return;                                  // no change (mount, or an unrelated dep changed) → no beat

    const next = beatFor(prev, status, kind, typeKey);
    if (!next || prefersReducedMotion()) return;                  // not a beat-worthy transition, or motion suppressed

    setBeat(next);
    const timer = setTimeout(() => setBeat(undefined), BEAT_MS);
    return () => clearTimeout(timer);
  }, [status, kind, typeKey]);

  return beat;
}

/** The beat for a transition, or undefined: the previous status must be non-terminal/absent and the new one a beat-worthy Success. */
function beatFor(prev: NodeStatus | undefined, next: NodeStatus | undefined, kind: NodeKind, typeKey: string): StatusBeat | undefined {
  if (next !== "Success") return undefined;                       // only a settle into Success beats
  if (prev === "Success" || prev === "Failure" || prev === "Skipped") return undefined;   // was already terminal → no real transition

  if (kind === "Trigger") return "ignite";
  if (kind === "Terminal") return "settle";
  if (ROUTING_TYPES.has(typeKey)) return "verdict";
  return undefined;
}

/** True when the viewer asked for reduced motion — the beat is then skipped (the CSS also suppresses it; returning undefined avoids the attribute churn). */
function prefersReducedMotion(): boolean {
  return typeof window !== "undefined" && typeof window.matchMedia === "function"
    && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}
