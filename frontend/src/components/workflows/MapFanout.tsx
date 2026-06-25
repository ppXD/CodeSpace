import { useState, type ReactNode } from "react";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";

import { fanBranches, fanBreakdown } from "./mapBranches";

/** A node status → the fan-out tile's render state: running folds Suspended, done folds Skipped, queued is Pending. */
function tileStateOf(status: NodeStatus): "running" | "done" | "failed" | "queued" {
  if (status === "Failure") return "failed";
  if (status === "Running" || status === "Suspended") return "running";
  if (status === "Pending") return "queued";
  return "done";
}

/**
 * The flow.map fan-out result, embedded under the canvas map/agent node — the same activity-terminal language as the
 * Activity tab, laid out in the graph. It renders a summary line (N branches + per-state counts), a per-branch status
 * dot strip you click to focus a branch, and the focused branch's terminal (its error / output / live agent run, via
 * `renderBranch`). Replaces the flat per-branch list so a 12-branch fan-out reads as ONE card, not twelve stacked rows.
 *
 * Pure layout + selection — the per-branch detail is INJECTED (`renderBranch`) rather than imported, so this stays
 * unit-testable without the agent-run data hooks the detail panel uses. Returns null for a non-map row set (the caller
 * then renders its plain list), so a loop / try / flat fan-out is untouched.
 */
export function MapFanout({ rows, renderBranch }: { rows: WorkflowRunNodeSummary[]; renderBranch: (row: WorkflowRunNodeSummary) => ReactNode }) {
  const branches = fanBranches(rows);
  // Keyed by the STABLE map element index, NOT the array slot: a 2s live poll can surface a slower lower-index
  // branch that re-sorts the array, so a slot index would silently swap the open terminal to a different branch.
  const [sel, setSel] = useState<number | null>(null);

  if (branches.length === 0) return null;

  const bd = fanBreakdown(branches);
  const cur = sel != null ? branches.find((b) => b.index === sel) ?? null : null;

  return (
    <div className="wf-rf-fanout nodrag nopan" onClick={(e) => e.stopPropagation()}>
      <div className="wf-rf-fanout-sum">
        <span className="wf-rf-fanout-total">{bd.total} {bd.total === 1 ? "branch" : "branches"}</span>
        {bd.done > 0 && <span data-state="done">{bd.done} done</span>}
        {bd.running > 0 && <span data-state="running">{bd.running} running</span>}
        {bd.failed > 0 && <span data-state="failed">{bd.failed} failed</span>}
        {bd.queued > 0 && <span data-state="queued">{bd.queued} queued</span>}
      </div>

      <div className="wf-rf-fanout-strip" role="tablist" aria-label="branches">
        {branches.map((b) => (
          <button
            key={b.index}
            type="button"
            className="wf-rf-fanout-dot"
            data-state={tileStateOf(b.row.status)}
            data-sel={b.index === sel || undefined}
            role="tab"
            aria-selected={b.index === sel}
            aria-label={`branch ${b.badge}, ${b.row.status}`}
            title={`${b.badge} · ${b.row.status}`}
            onClick={() => setSel(b.index === sel ? null : b.index)}
          />
        ))}
      </div>

      {cur && (
        <div className="wf-rf-fanout-term nowheel">
          <div className="wf-rf-fanout-term-h">
            <span className="wf-rf-fanout-term-ix">{cur.badge}</span>
            <span className="wf-rf-fanout-term-st" data-status={cur.row.status.toLowerCase()}>{cur.row.status}</span>
          </div>
          {renderBranch(cur.row)}
        </div>
      )}
    </div>
  );
}
