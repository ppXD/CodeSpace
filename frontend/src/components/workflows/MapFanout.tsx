import { useEffect, useRef, useState, type ReactNode } from "react";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";
import { useNodeLiveContext } from "@/hooks/use-run-live";

import { fanBranches, fanBreakdown, MAP_CONTAINER_KIND, parseIterationKey, type FanBranch } from "./mapBranches";
import { RerunMenu, type RerunTarget } from "./RerunMenu";

/** The rerun target for a focused fan-out item — only a TOP-LEVEL flow.map branch (single-segment key + map container) is rerunnable; a nested / loop body returns null. */
function mapItemTarget(branches: readonly FanBranch[], focused: FanBranch): RerunTarget | null {
  const segments = parseIterationKey(focused.row.iterationKey);
  if (segments.length !== 1 || focused.row.containerKind !== MAP_CONTAINER_KIND) return null;
  return {
    kind: "mapItem",
    mapNodeId: segments[0].containerId,
    focusedIndex: focused.index,
    failedIndices: branches.filter((b) => b.row.status === "Failure").map((b) => b.index),
    totalCount: branches.length,
  };
}

/** A node status → the fan-out tile's render state: waiting is a parked Suspend, done folds Skipped, queued is Pending. */
function tileStateOf(status: NodeStatus): "running" | "done" | "failed" | "queued" | "waiting" {
  if (status === "Failure") return "failed";
  if (status === "Suspended") return "waiting";
  if (status === "Running") return "running";
  if (status === "Pending") return "queued";
  return "done";
}

/** The summary/strip counts a fan-out paints: the rows-derived breakdown, overlaid by the live store's branch tallies when present. */
interface FanCounts {
  total: number;
  done: number;
  running: number;
  failed: number;
  waiting: number;
  queued: number;
}

/**
 * Merge the rows-derived breakdown with the live-store branch tallies (when a run SSE is feeding this node). Live wins
 * for the four moving counts (immediacy) and supplies the planned `total` once a BE PR emits it; `queued` is the remainder
 * (still-Pending rows PLUS any known-but-not-yet-started branches when total exceeds the rendered branches). With no live
 * store the result is exactly the rows breakdown — today's behavior, so a footer degrades cleanly.
 */
function mergeCounts(bd: ReturnType<typeof fanBreakdown>, live: { done: number; failed: number; running: number; waiting: number; total?: number } | undefined): FanCounts {
  const total = live?.total ?? bd.total;
  const done = live?.done ?? bd.done;
  const running = live?.running ?? bd.running;
  const failed = live?.failed ?? bd.failed;
  const waiting = live?.waiting ?? bd.waiting;

  return { total, done, running, failed, waiting, queued: Math.max(0, total - done - running - failed - waiting) };
}

/**
 * The flow.map fan-out result, embedded under the canvas map/agent node — the same activity-terminal language as the
 * Activity tab, laid out in the graph. It renders a summary line (N branches + per-state counts, incl. parked "等待"), a
 * per-branch status dot strip you click to focus a branch (only the branch that JUST flipped pulses), and the focused
 * branch's terminal (its error / output / live agent run, via `renderBranch`). When every branch has settled, the strip
 * folds into a single "results [N]" chip. Reads the canvas live store (when present) for immediate counts + the planned
 * total; degrades to the poll-derived rows otherwise.
 *
 * Pure layout + selection — the per-branch detail is INJECTED (`renderBranch`) rather than imported, so this stays
 * unit-testable without the agent-run data hooks the detail panel uses. Returns null for a non-map row set (the caller
 * then renders its plain list), so a loop / try / flat fan-out is untouched.
 */
export function MapFanout({ rows, renderBranch, inline }: { rows: WorkflowRunNodeSummary[]; renderBranch: (row: WorkflowRunNodeSummary) => ReactNode; inline?: boolean }) {
  const branches = fanBranches(rows);
  // Keyed by the STABLE map element index, NOT the array slot: a 2s live poll can surface a slower lower-index
  // branch that re-sorts the array, so a slot index would silently swap the open terminal to a different branch.
  const [sel, setSel] = useState<number | null>(null);
  const [collapsed, setCollapsed] = useState(false);

  // Live overlay — the canvas run SSE keyed by THIS node id. Null outside a run / provider (editor, tests) → rows only.
  const live = useNodeLiveContext(rows[0]?.nodeId ?? "");

  // Fresh-cell pulse — mark ONLY the branch(es) whose status changed since the last render, for ~1.2s. A steady running
  // cell stays a solid accent dot; without this a 12-branch map would be a christmas tree of infinite pulses.
  const statusSig = branches.map((b) => `${b.row.iterationKey}=${b.row.status}`).join("|");
  const prevStatus = useRef<Map<string, NodeStatus>>(new Map());
  const [fresh, setFresh] = useState<ReadonlySet<string>>(() => new Set());

  useEffect(() => {
    const prev = prevStatus.current;
    const flipped: string[] = [];
    for (const b of branches) {
      const was = prev.get(b.row.iterationKey);
      if (was !== undefined && was !== b.row.status) flipped.push(b.row.iterationKey);
      prev.set(b.row.iterationKey, b.row.status);
    }

    if (flipped.length === 0) return;

    // eslint-disable-next-line react-hooks/set-state-in-effect -- intentional one-shot fresh-cell flash seeded from the just-computed branch-flip set; keyed on statusSig so it does not loop.
    setFresh((s) => new Set([...s, ...flipped]));
    const timer = setTimeout(() => setFresh((s) => { const n = new Set(s); for (const k of flipped) n.delete(k); return n; }), 1200);
    return () => clearTimeout(timer);
    // `branches` is derived from `statusSig` (same key/status pairs), so the closure is in sync; keying on the signature
    // avoids re-running every render (fanBranches returns a fresh array each time).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [statusSig]);

  if (branches.length === 0) return null;

  const c = mergeCounts(fanBreakdown(branches), live?.branches);
  const cur = sel != null ? branches.find((b) => b.index === sel) ?? null : null;
  const allSettled = c.running === 0 && c.waiting === 0 && c.queued === 0;
  const showStrip = !(allSettled && collapsed);
  const placeholders = Math.max(0, c.total - branches.length);

  // `inline` = rendered INSIDE a collapsed fan-out card node (static flow, the card frames it); the default floats
  // the panel below a node (absolute) like the coze result bar.
  return (
    <div className={`wf-rf-fanout nodrag nopan${inline ? " wf-rf-fanout-inline" : ""}`} onClick={(e) => e.stopPropagation()}>
      <div className="wf-rf-fanout-sum">
        <span className="wf-rf-fanout-total">{c.total} {c.total === 1 ? "branch" : "branches"}</span>
        {c.done > 0 && <span data-state="done">{c.done} done</span>}
        {c.running > 0 && <span data-state="running">{c.running} running</span>}
        {c.waiting > 0 && <span data-state="waiting">· {c.waiting} 等待</span>}
        {c.failed > 0 && <span data-state="failed">{c.failed} failed</span>}
        {c.queued > 0 && <span data-state="queued">{c.queued} queued</span>}
      </div>

      {allSettled && (
        <button
          type="button"
          className="wf-rf-fanout-reduce"
          data-open={!collapsed || undefined}
          aria-expanded={!collapsed}
          title={collapsed ? "Show branches" : "Fold into results"}
          onClick={() => setCollapsed((v) => !v)}
        >
          <span className="wf-rf-fanout-reduce-key">results</span>
          <span className="wf-rf-fanout-reduce-n">[{c.total}]</span>
        </button>
      )}

      {showStrip && (
        <div className="wf-rf-fanout-strip" role="tablist" aria-label="branches">
          {branches.map((b) => (
            <button
              key={b.index}
              type="button"
              className="wf-rf-fanout-dot"
              data-state={tileStateOf(b.row.status)}
              data-sel={b.index === sel || undefined}
              data-fresh={fresh.has(b.row.iterationKey) || undefined}
              role="tab"
              aria-selected={b.index === sel}
              aria-label={`branch ${b.badge}, ${b.row.status}`}
              title={`${b.badge} · ${b.row.status}`}
              onClick={() => setSel(b.index === sel ? null : b.index)}
            />
          ))}
          {Array.from({ length: placeholders }, (_, i) => (
            <span key={`q${i}`} className="wf-rf-fanout-dot" data-state="queued" data-placeholder aria-hidden="true" />
          ))}
        </div>
      )}

      {cur && (
        <div className="wf-rf-fanout-term nowheel">
          <div className="wf-rf-fanout-term-h">
            <span className="wf-rf-fanout-term-ix">{cur.badge}</span>
            <span className="wf-rf-fanout-term-st" data-status={cur.row.status.toLowerCase()}>{cur.row.status}</span>
            {(() => { const t = mapItemTarget(branches, cur); return t && <RerunMenu target={t} className="wf-rerun-onrow" />; })()}
          </div>
          {renderBranch(cur.row)}
        </div>
      )}
    </div>
  );
}
