import type { Node } from "@xyflow/react";

import type { WorkflowNodeData } from "./WorkflowNode";

/**
 * The RunCanvas rebuilds its whole node array on every 2s poll (a fresh status/rows Map each time), so
 * without this every node gets a new `data` object and React Flow re-renders EVERY card — even the ones
 * whose run state didn't move. These helpers diff the freshly-built nodes against the previous array and
 * hand back the PREVIOUS object reference for any node whose run overlay is unchanged, so React Flow's
 * per-node memo skips it. On a 40-node graph a single 2s poll then re-renders only the nodes that moved
 * (typically 1-2), not all 40.
 *
 * Pure + deterministic → unit-tested. Only the RUN OVERLAY is fingerprinted (status, per-row
 * status/timing/error, rerun eligibility, fan-out shape, hidden). The static base data (name, icon,
 * kind, badges, position, size) can't change within a run — the definition is a version-pinned snapshot —
 * so the caller resets the cache when the definition / manifests identity changes, and the fingerprint
 * needn't cover them.
 */
export function nodeRunFingerprint(node: Node<WorkflowNodeData>): string {
  const d = node.data;
  const rows = d.runRows ?? [];
  const rowsFp = rows.map((r) => `${r.status}:${r.startedAt ?? ""}:${r.completedAt ?? ""}:${r.error ?? ""}`).join("|");
  const fanFp = d.fanout ? d.fanout.map((r) => `${r.status}:${r.startedAt ?? ""}:${r.completedAt ?? ""}`).join("|") : "";
  return `${d.runStatus ?? ""}#${d.rerunnableFromHere ? 1 : 0}#${node.hidden ? 1 : 0}#${rowsFp}#${fanFp}`;
}

/**
 * Reconcile a freshly-built node array against the previous one: for every node whose run fingerprint
 * matches its previous self, return the PREVIOUS object (reference-stable → React Flow skips it); for a
 * changed or brand-new node, return the fresh object. Output preserves `next`'s order and membership, so
 * added / removed nodes are handled for free. Pass `prev = null` (e.g. after a definition change) to take
 * every fresh node as-is.
 */
export function patchNodes(
  prev: readonly Node<WorkflowNodeData>[] | null,
  next: Node<WorkflowNodeData>[],
): Node<WorkflowNodeData>[] {
  if (!prev || prev.length === 0) return next;
  const prevById = new Map(prev.map((n) => [n.id, n]));
  return next.map((n) => {
    const p = prevById.get(n.id);
    return p && nodeRunFingerprint(p) === nodeRunFingerprint(n) ? p : n;
  });
}
