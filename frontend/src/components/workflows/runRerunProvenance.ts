import type { RunAttempt } from "@/api/workflows";

/**
 * Per-node rerun history, derived from a lineage's attempt ladder. A node's history is the ordered list of attempts
 * that re-ran it — i.e. every attempt whose `rerunFromNodeId` is that node (the map node, for a branch rerun). Plus
 * the original (attempt 1) is implicitly the first run of every node, but it isn't a RErun, so it's not counted here:
 * the badge means "re-ran N times", and the history shows those rerun attempts. A node never re-run has no entry.
 */
export function rerunsByNode(attempts: readonly RunAttempt[]): Map<string, RunAttempt[]> {
  const byNode = new Map<string, RunAttempt[]>();

  for (const a of attempts) {
    if (!a.rerunFromNodeId) continue;
    const list = byNode.get(a.rerunFromNodeId) ?? [];
    list.push(a);
    byNode.set(a.rerunFromNodeId, list);
  }

  return byNode;
}

/** The attempts that re-ran one node (empty if it was never re-run), oldest first. */
export function nodeReruns(provenance: Map<string, RunAttempt[]>, nodeId: string): RunAttempt[] {
  return provenance.get(nodeId) ?? [];
}
