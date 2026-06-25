import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";

/**
 * Map-branch observability — pure parsing + grouping over the engine's iteration-key format.
 *
 * The engine keys every fanned-out body node under a per-branch iteration key built by
 * `CombineIterationKey` (WorkflowEngine.cs): a top-level map branch is `"<mapId>#<i>"`, and a
 * nested map-in-map (or loop/try inside a map) branch composes with `/`, e.g.
 * `"<outerMapId>#<i>/<innerMapId>#<j>"`. A non-iterated top-level node has an EMPTY key.
 *
 * CRUCIAL: a flow.loop body node is keyed `"<loopId>#<i>"` — the SAME shape as a map branch — and a
 * flow.try body node is keyed `"<tryId>"` (no `#`). The iteration key alone therefore can't tell a map
 * branch apart from a loop iteration, so we ONLY treat a row as a map branch when the backend stamped its
 * `containerKind` as `"flow.map"` (the typeKey of the container owning its innermost iteration). Loop /
 * try rows fall through as plain rows — exactly as they rendered before this observability landed.
 *
 * The run-detail trace renders body rows keyed only by nodeId today, so a K-branch map shows K
 * identical unlabeled rows. These helpers parse the key into its `<containerId>#<index>` segments
 * so the view can: badge each MAP row `#i` (or `#i/#j` when nested), GROUP rows by their owning map,
 * and roll up done / failed / total per map. A run with no map rows produces zero branch info, so a
 * non-map run (loop-only, try-only, or flat) renders exactly as before.
 */

/** The container typeKey that owns a map fan-out (mirrors backend FlowMapNode.TypeKey). */
const MAP_CONTAINER_KIND = "flow.map";

/** True for a row that belongs to a flow.map element-branch (vs a loop / try / top-level row). */
function isMapBranchNode(node: WorkflowRunNodeSummary): boolean {
  return node.containerKind === MAP_CONTAINER_KIND && node.iterationKey.length > 0;
}

/** Terminal node statuses (the engine will not touch the row again). */
function isTerminalStatus(status: NodeStatus): boolean {
  return status === "Success" || status === "Failure" || status === "Skipped";
}

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
 * The branch badge for a node row: `#i` at one level, `#i/#j` nested, `""` (no badge) for a node that
 * isn't a flow.map element-branch (top-level, loop iteration, or try body). Mirrors the engine key's
 * `/`-joined nesting so a reader can map a map row straight back to its element index at each map level.
 * Gated on `containerKind` so a loop body row — whose key has the same `<id>#<i>` shape — stays unbadged.
 */
export function branchBadge(node: WorkflowRunNodeSummary): string {
  if (!isMapBranchNode(node)) return "";

  const segments = parseIterationKey(node.iterationKey);
  if (segments.length === 0) return "";
  return segments.map((s) => `#${s.index}`).join("/");
}

/** Per-map rollup over its branch rows: distinct branches seen, and how each settled. */
export interface MapRollup {
  /** The map node id this group fans out from (the FINAL segment's containerId — the innermost map for a nested branch). */
  mapId: string;
  /** Distinct element-branch indices observed for this map, ascending. */
  branchIndices: number[];
  total: number;
  /** Branches that finished cleanly: EVERY row terminal and NONE failed. Excludes still-in-flight branches. */
  done: number;
  /** Branches with at least one failed (or skipped-after-failure) row — the engine's abandon / terminate marker. */
  failed: number;
}

/** A branch's roll-up state, folded over its (possibly many) body rows. */
interface BranchState {
  /** Any row in this branch is a Failure. */
  failed: boolean;
  /** Every row seen so far is terminal (Success / Failure / Skipped) — no Pending / Running / Suspended. */
  allTerminal: boolean;
}

/**
 * Group a run's nodes into branch groups keyed by the MAP they belong to, deriving a per-map
 * done / failed / total rollup. Only flow.map element-branch rows participate (gated on `containerKind`,
 * since a loop body row shares the `<id>#<i>` key shape). The group key is the iteration key MINUS its
 * final `#index` — i.e. the path of enclosing branches plus the map id — so two different elements of the
 * same map land in one group while two different maps (or two passes of an OUTER map) stay separate.
 *
 * Counts are per DISTINCT branch index (not per row): a branch with several body nodes counts once.
 *  - `failed`: ANY of the branch's rows is a Failure.
 *  - `done`: the branch is fully settled — EVERY row terminal AND none failed. A branch with a
 *    Pending / Running / Suspended row is in-flight: it's in `total` but neither `done` nor `failed`,
 *    so a live map reads "1/3 done" while two branches are still running (not a misleading "3/3").
 *
 * A run with no map rows (loop-only, try-only, or flat) produces zero groups and the caller renders the
 * flat list unchanged.
 */
export function groupMapBranches(nodes: readonly WorkflowRunNodeSummary[]): MapRollup[] {
  // groupKey → { mapId, branchIndex → folded BranchState }
  const groups = new Map<string, { mapId: string; branches: Map<number, BranchState> }>();

  for (const n of nodes) {
    if (!isMapBranchNode(n)) continue;

    const segments = parseIterationKey(n.iterationKey);
    if (segments.length === 0) continue;

    const leaf = segments[segments.length - 1];
    const groupKey = branchGroupKey(n.iterationKey);

    let group = groups.get(groupKey);
    if (!group) {
      group = { mapId: leaf.containerId, branches: new Map() };
      groups.set(groupKey, group);
    }

    const prior = group.branches.get(leaf.index) ?? { failed: false, allTerminal: true };
    group.branches.set(leaf.index, {
      failed: prior.failed || n.status === "Failure",
      allTerminal: prior.allTerminal && isTerminalStatus(n.status),
    });
  }

  return Array.from(groups.values()).map((g) => {
    const indices = Array.from(g.branches.keys()).sort((a, b) => a - b);
    const states = indices.map((i) => g.branches.get(i)!);
    const failed = states.filter((s) => s.failed).length;
    const done = states.filter((s) => !s.failed && s.allTerminal).length;
    return { mapId: g.mapId, branchIndices: indices, total: indices.length, done, failed };
  });
}

/** Stable group key for one node row — the iteration key without its leaf `#index`. `""` for a top-level node. */
export function branchGroupKey(iterationKey: string): string {
  const hash = iterationKey.lastIndexOf("#");
  return hash < 0 ? "" : iterationKey.slice(0, hash);
}

/** One fanned-out branch for the canvas fan-out panel: its element index, badge (`#i` / `#i/#j`), and the row to inspect. */
export interface FanBranch {
  index: number;
  badge: string;
  row: WorkflowRunNodeSummary;
}

/**
 * The flow.map element-branches a fanned-out body node ran — one entry per element index, ascending. Only
 * flow.map branch rows participate (gated on `containerKind`, since a loop body row shares the `<id>#<i>` shape),
 * so a non-map (loop / try / flat) row set yields `[]` and the caller renders the plain list unchanged. When a
 * branch index recurs (a body with several nodes per element), the FIRST row seen represents it.
 */
export function fanBranches(rows: readonly WorkflowRunNodeSummary[]): FanBranch[] {
  const byIndex = new Map<number, FanBranch>();

  for (const row of rows) {
    if (!isMapBranchNode(row)) continue;

    const segments = parseIterationKey(row.iterationKey);
    if (segments.length === 0) continue;

    const index = segments[segments.length - 1].index;
    if (byIndex.has(index)) continue;

    byIndex.set(index, { index, badge: segments.map((s) => `#${s.index}`).join("/"), row });
  }

  return Array.from(byIndex.values()).sort((a, b) => a.index - b.index);
}

/** A fan-out's per-state branch counts for the summary line (running folds Suspended; done folds Skipped). */
export interface FanBreakdown {
  total: number;
  done: number;
  running: number;
  failed: number;
  queued: number;
}

export function fanBreakdown(branches: readonly FanBranch[]): FanBreakdown {
  const b: FanBreakdown = { total: branches.length, done: 0, running: 0, failed: 0, queued: 0 };

  for (const { row } of branches) {
    if (row.status === "Failure") b.failed++;
    else if (row.status === "Running" || row.status === "Suspended") b.running++;
    else if (row.status === "Pending") b.queued++;
    else b.done++;   // Success / Skipped — settled without failing
  }

  return b;
}
