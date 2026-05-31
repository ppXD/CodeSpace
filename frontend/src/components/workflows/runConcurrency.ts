import type { WorkflowRunNodeSummary } from "@/api/workflows";

/** A node's wall-clock interval [start, end] in epoch ms, or null when it never started. */
function interval(n: WorkflowRunNodeSummary): readonly [number, number] | null {
  if (!n.startedAt) return null;
  const start = new Date(n.startedAt).getTime();
  if (!Number.isFinite(start)) return null;
  const end = n.completedAt ? new Date(n.completedAt).getTime() : start;
  return [start, Number.isFinite(end) ? Math.max(start, end) : start];
}

/** Stable per-node-execution key — matches the run trace's React key (a node runs once per iteration). */
export const runNodeKey = (n: WorkflowRunNodeSummary): string => `${n.nodeId}:${n.iterationKey}`;

/**
 * The set of node keys whose execution interval overlapped at least one OTHER node's — i.e. they ran
 * concurrently (the engine's intra-run parallel wave, top-level or inside a loop body). Two intervals
 * [a1,a2] and [b1,b2] overlap iff `a1 < b2 && b1 < a2`; a touching handoff (one ends exactly as the
 * next starts) is NOT overlap, so a sequential run yields an empty set. Pure + deterministic → the
 * run-detail trace badges these so a parallel run is legible at a glance.
 */
export function concurrentNodeKeys(nodes: readonly WorkflowRunNodeSummary[]): Set<string> {
  const items = nodes
    .map((n) => ({ key: runNodeKey(n), iv: interval(n) }))
    .filter((x): x is { key: string; iv: readonly [number, number] } => x.iv !== null);

  const overlapping = new Set<string>();
  for (let i = 0; i < items.length; i++) {
    for (let j = i + 1; j < items.length; j++) {
      const [a1, a2] = items[i].iv;
      const [b1, b2] = items[j].iv;
      if (a1 < b2 && b1 < a2) {
        overlapping.add(items[i].key);
        overlapping.add(items[j].key);
      }
    }
  }
  return overlapping;
}
