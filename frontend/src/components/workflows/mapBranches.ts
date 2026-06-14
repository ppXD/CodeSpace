import type { WorkflowRunNodeSummary } from "@/api/workflows";

/**
 * Map-branch observability — pure parsing + grouping over the engine's iteration-key format.
 *
 * The engine keys every fanned-out body node under a per-branch iteration key built by
 * `CombineIterationKey` (WorkflowEngine.cs): a top-level map branch is `"<mapId>#<i>"`, and a
 * nested map-in-map (or loop/try inside a map) branch composes with `/`, e.g.
 * `"<outerMapId>#<i>/<innerMapId>#<j>"`. A non-iterated top-level node has an EMPTY key.
 *
 * The run-detail trace renders body rows keyed only by nodeId today, so a K-branch map shows K
 * identical unlabeled rows. These helpers parse the key into its `<containerId>#<index>` segments
 * so the view can: badge each row `#i` (or `#i/#j` when nested), GROUP rows by their owning map,
 * and roll up done / failed / total per map. A run with no iteration keys parses to zero branch
 * segments, so a non-map run renders exactly as before (every node falls in the "top-level" bucket).
 */

/** One `<containerId>#<index>` segment of an iteration key. */
export interface BranchSegment {
  containerId: string;
  index: number;
}

/**
 * Parse an iteration key into its ordered branch segments. `""` → `[]` (a top-level, non-iterated
 * node). `"map#2"` → `[{ map, 2 }]`. `"outer#0/inner#3"` → `[{ outer, 0 }, { inner, 3 }]`. A segment
 * that doesn't match `<id>#<int>` is skipped (forward-compatible: an unknown future key shape simply
 * yields no branch badge rather than a crash).
 */
export function parseIterationKey(iterationKey: string): BranchSegment[] {
  if (!iterationKey) return [];

  const segments: BranchSegment[] = [];
  for (const part of iterationKey.split("/")) {
    const hash = part.lastIndexOf("#");
    if (hash < 0) continue;

    const containerId = part.slice(0, hash);
    const index = Number(part.slice(hash + 1));
    if (!containerId || !Number.isInteger(index) || index < 0) continue;

    segments.push({ containerId, index });
  }
  return segments;
}

/**
 * The branch badge for an iteration key: `#i` at one level, `#i/#j` nested, `""` (no badge) for a
 * top-level node. Mirrors the engine key's `/`-joined nesting so a reader can map a row straight back
 * to its element index at each map level.
 */
export function branchBadge(iterationKey: string): string {
  const segments = parseIterationKey(iterationKey);
  if (segments.length === 0) return "";
  return segments.map((s) => `#${s.index}`).join("/");
}

/** Per-map rollup over its branch rows: distinct branches seen, and how many of them failed. */
export interface MapRollup {
  /** The map node id this group fans out from (the FINAL segment's containerId — the innermost map for a nested branch). */
  mapId: string;
  /** Distinct element-branch indices observed for this map, ascending. */
  branchIndices: number[];
  total: number;
  done: number;
  failed: number;
}

/**
 * Group a run's nodes into branch groups keyed by the map they belong to, deriving a per-map
 * done / failed / total rollup. The group key is the iteration key MINUS its final `#index` — i.e.
 * the path of enclosing branches plus the map id — so two different elements of the same map land in
 * one group while two different maps (or two passes of an OUTER map) stay separate.
 *
 * `done` / `failed` are counted per DISTINCT branch index (not per row): a branch with several body
 * nodes still counts once, and is "failed" iff ANY of its rows is a Failure (the engine's abandon /
 * terminate marker). Nodes with an empty iteration key (top-level, non-map) are excluded entirely —
 * so a non-map run produces zero groups and the caller renders the flat list unchanged.
 */
export function groupMapBranches(nodes: readonly WorkflowRunNodeSummary[]): MapRollup[] {
  // groupKey → mapId → (branchIndex → failed?)
  const groups = new Map<string, { mapId: string; branches: Map<number, boolean> }>();

  for (const n of nodes) {
    const segments = parseIterationKey(n.iterationKey);
    if (segments.length === 0) continue;

    const leaf = segments[segments.length - 1];
    const groupKey = n.iterationKey.slice(0, n.iterationKey.lastIndexOf("#"));

    let group = groups.get(groupKey);
    if (!group) {
      group = { mapId: leaf.containerId, branches: new Map() };
      groups.set(groupKey, group);
    }

    const wasFailed = group.branches.get(leaf.index) ?? false;
    group.branches.set(leaf.index, wasFailed || n.status === "Failure");
  }

  return Array.from(groups.values()).map((g) => {
    const indices = Array.from(g.branches.keys()).sort((a, b) => a - b);
    const failed = indices.filter((i) => g.branches.get(i)).length;
    return { mapId: g.mapId, branchIndices: indices, total: indices.length, done: indices.length - failed, failed };
  });
}

/** Stable group key for one node row — the iteration key without its leaf `#index`. `""` for a top-level node. */
export function branchGroupKey(iterationKey: string): string {
  const hash = iterationKey.lastIndexOf("#");
  return hash < 0 ? "" : iterationKey.slice(0, hash);
}

/** A node status that counts as a completed-but-failed branch leaf (the engine's abandon / terminate marker). */
export function isFailureStatus(status: NodeStatus): boolean {
  return status === "Failure";
}
