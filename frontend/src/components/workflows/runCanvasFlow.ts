import type { NodeStatus } from "@/api/workflows";

/**
 * n8n-style data-flow styling for one run edge, derived from its endpoints: the connection the run actually took
 * lights up — terracotta + animated dashes for data flowing INTO the live node, sage for a completed hop, danger
 * into a failed node — while a branch the run skipped stays the default faint line. `runActive` is false once the
 * run is terminal: a node still reading Running/Suspended is then stale (e.g. parked on agents a cancel killed), so
 * the edge shows a static stopped line, never the live flow.
 */
export function runEdgeFlow(src?: NodeStatus, tgt?: NodeStatus, runActive = true): { stroke?: string; animated?: boolean } {
  if (src !== "Success") return {};                                            // nothing flowed out of the source yet

  if (tgt === "Running" || tgt === "Suspended")
    return runActive ? { stroke: "#D97757", animated: true } : { stroke: "#B7AEA1" };

  if (tgt === "Failure") return { stroke: "#C0623D" };                         // the hop into a failed node
  if (tgt === "Success") return { stroke: "#5FA882" };                         // a completed hop on the taken path

  return {};                                                                    // target pending/skipped → branch not taken
}
