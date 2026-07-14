import type { NodeStatus } from "@/api/workflows";

/**
 * A semantic class for a run edge's state, beyond its stroke — so the canvas can style the hop the run
 * actually walked (`taken`), a branch it declined (`dead`, dimmed), or a just-settled routing choice
 * (`verdict`, a brief highlight owned by B7). See runEdgeFlow / definitionToRunEdges.
 */
export type RunEdgeClass = "taken" | "dead" | "verdict";

/**
 * n8n-style data-flow styling for one run edge, derived from its endpoints: the connection the run actually took
 * lights up — terracotta + animated dashes for data flowing INTO the live node, sage for a completed hop, danger
 * into a failed node — while a branch the run skipped is dimmed. `runActive` is false once the run is terminal: a
 * node still reading Running/Suspended is then stale (e.g. parked on agents a cancel killed), so the edge shows a
 * static stopped line, never the live flow. The optional `cls` tags the edge's state for the canvas to style.
 */
export function runEdgeFlow(src?: NodeStatus, tgt?: NodeStatus, runActive = true): { stroke?: string; animated?: boolean; cls?: RunEdgeClass } {
  if (src !== "Success") return {};                                            // nothing flowed out of the source yet

  if (tgt === "Running" || tgt === "Suspended")
    return runActive ? { stroke: "#D97757", animated: true } : { stroke: "#B7AEA1" };

  if (tgt === "Failure") return { stroke: "#C0623D" };                         // the hop into a failed node

  // Both endpoints Success — the hop the run actually walked. Keep the sage stroke; tag it `taken`.
  //
  // We deliberately do NOT emit `verdict` here. A "just-settled routing choice" needs to know the target
  // became Success THIS poll, which is not derivable from the two endpoint statuses alone (a live run has
  // many long-settled Success→Success hops that would all falsely light up). The 1.2s verdict flash needs a
  // target-fresh signal.
  //
  // TODO(B7): the edge verdict glow is DEFERRED. B7 ships the CARD verdict beat (useStatusBeat → data-beat on
  // logic.if / flow.decision / flow.try, etc.), but that "just beat verdict" signal lives in each node's local
  // component state, not in the statuses map this fold reads. Marking the outgoing taken-edge would mean either
  // lifting the transition-detection to RunCanvas (a real refactor of the edge fold + this signature) or a
  // child→parent reporting channel — heavier plumbing than the edge flash warrants right now. Until then a
  // completed hop stays `taken`, never a faked `verdict`.
  if (tgt === "Success") return { stroke: "#5FA882", cls: "taken" };

  // source Success + target Skipped — a branch the run declined. Muted stroke + `dead` so the canvas dims it.
  if (tgt === "Skipped") return { stroke: "#B7AEA1", cls: "dead" };

  return {};                                                                    // target pending → not reached yet
}
