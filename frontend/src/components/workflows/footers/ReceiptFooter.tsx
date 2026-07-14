/* eslint-disable react-refresh/only-export-components -- this module co-locates its render component with the small pure helpers (glyphs, formatters, digest/label builders) that only it and its sibling footers use; fast-refresh granularity is moot for these. */
import { useContext, useState, type ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";

import { AgentRunTimeline } from "../AgentRunTimeline";
import { AgentToolCalls } from "../AgentToolCalls";
import { JsonView } from "../JsonView";
import { fanoutSummary, nodeIterationLabel } from "../mapBranches";
import { RunOpenContext } from "../runOpenContext";
import type { NodeFooterProps } from "./index";

/**
 * Coze/Dify-style run-result footer that hangs UNDER a node in a run view: a compact "status · duration"
 * bar that expands in place to reveal the node's output (and error). The graph flows left→right, so the
 * space below each node is free — the bar floats there without overlapping neighbours. Renders only when
 * the node has run rows (run view); the editor card never carries it.
 */
export function ReceiptFooter({ status, rows, title, labelSlot }: NodeFooterProps & { labelSlot?: ReactNode }) {
  const [open, setOpen] = useState(false);
  if (status === "Pending") return null;                 // not reached yet → no footer (matches coze)

  const durationMs = aggregateDurationMs(rows);
  const expandable = rows.some(isRowExpandable);
  // An embedded agent timeline / sub-workflow link needs more room than a node-width panel — widen it.
  const rich = rows.some((r) => !!r.agentRunId || !!r.childRunId);

  return (
    <div className="wf-rf-result nodrag nopan" data-status={status.toLowerCase()} data-open={open || undefined}>
      <button
        type="button"
        className="wf-rf-result-bar"
        data-expandable={expandable || undefined}
        aria-expanded={expandable ? open : undefined}
        title={title}
        onClick={(e) => { e.stopPropagation(); if (expandable) setOpen((v) => !v); }}
      >
        <span className="wf-rf-result-glyph" aria-hidden="true">{resultGlyph(status)}</span>
        <span className="wf-rf-result-label">{labelSlot ?? resultLabel(status, rows)}</span>
        {durationMs != null && <span className="wf-rf-result-dur">{formatDuration(durationMs)}</span>}
        {expandable && <span className="wf-rf-result-caret" aria-hidden="true"><Ic.ChevronDown size={12} /></span>}
      </button>
      {open && expandable && (
        <div className="wf-rf-result-panel nowheel nodrag" data-rich={rich || undefined} onClick={(e) => e.stopPropagation()}>
          {rows.length === 1
            ? <RunRowDetail row={rows[0]} />
            : rows.map((r, i) => {
                // The REAL iteration label, parsed from the row's iterationKey: a map element index (#i, or #i/#j
                // nested) OR a supervisor turn (turn 2 · parked) — NOT the array position, which is StartedAt
                // order. "" for a loop/try row, which stays unlabeled, exactly as the timeline view does.
                const badge = nodeIterationLabel(r);
                return (
                  <div key={`${r.nodeId}:${r.iterationKey}:${i}`} className="wf-rf-result-branch">
                    <div className="wf-rf-result-branch-h">
                      {badge && <span className="wf-rf-result-branch-ix">{badge}</span>}
                      <span className="wf-rf-result-branch-st" data-status={r.status.toLowerCase()}>{r.status}</span>
                    </div>
                    <RunRowDetail row={r} />
                  </div>
                );
              })}
        </div>
      )}
    </div>
  );
}

/**
 * One run row's detail inside the result panel — error first, then output, then input. For an `agent.run`
 * step it also embeds the LIVE agent run (status + event timeline + governed tool-call audit), so you watch
 * the agent work without leaving the canvas; for a `flow.subworkflow` step it offers to open the child run.
 * Falls back to "no output" only when the row carries nothing at all.
 */
export function RunRowDetail({ row }: { row: WorkflowRunNodeSummary }) {
  const onOpenRun = useContext(RunOpenContext);
  const hasOut = hasRunContent(row.outputs);
  const hasIn = hasRunContent(row.inputs);
  const bare = !row.error && !hasOut && !hasIn && !row.agentRunId && !row.childRunId;

  return (
    <>
      {row.error && <pre className="wf-rf-result-err">{row.error}</pre>}
      {hasOut && (
        <div className="wf-rf-result-block">
          <div className="wf-rf-result-block-h">Output</div>
          <JsonView data={row.outputs} />
        </div>
      )}
      {hasIn && (
        <div className="wf-rf-result-block">
          <div className="wf-rf-result-block-h">Input</div>
          <JsonView data={row.inputs} />
        </div>
      )}
      {row.agentRunId && (
        <div className="wf-rf-result-block">
          <AgentRunTimeline agentRunId={row.agentRunId} />
          <AgentToolCalls agentRunId={row.agentRunId} />
        </div>
      )}
      {row.childRunId && onOpenRun && (
        <button type="button" className="wf-rf-result-open" onClick={() => onOpenRun(row.childRunId!)}>
          <Ic.ArrowOut size={12} /> Open sub-workflow run
        </button>
      )}
      {bare && <div className="wf-rf-result-empty">No output recorded.</div>}
    </>
  );
}

/** A row earns the expand caret when it carries anything inspectable — output, input, error, an agent run, or a child run. */
export function isRowExpandable(row: WorkflowRunNodeSummary): boolean {
  return hasRunContent(row.outputs) || !!row.error || hasRunContent(row.inputs) || !!row.agentRunId || !!row.childRunId;
}

/** The status glyph in the result bar — mirrors the corner badge's tone vocabulary. */
export function resultGlyph(status: NodeStatus) {
  if (status === "Success") return <Ic.Check size={12} />;
  if (status === "Failure") return <Ic.X size={12} />;
  if (status === "Suspended") return <Ic.Pause size={12} />;
  if (status === "Skipped") return <Ic.Dot size={14} />;
  return <span className="wf-rf-status-spin" />;            // Running
}

/** "Success" / "Failure" / … — appends "· N {branches|turns|runs}" when a node ran multiple iterations (the noun matches what it ran: map branches, supervisor turns, or a neutral fallback). */
function resultLabel(status: NodeStatus, rows: WorkflowRunNodeSummary[]): string {
  const { count, noun } = fanoutSummary(rows);
  return count > 1 ? `${status} · ${count} ${noun}` : status;
}

/**
 * Total wall-clock for a node's row(s): earliest start → latest completion. Returns null while any row is
 * still running (no completion yet) so the bar shows just the live status, no misleading duration.
 */
export function aggregateDurationMs(rows: WorkflowRunNodeSummary[]): number | null {
  const starts: number[] = [];
  let latestEnd = 0;
  for (const r of rows) {
    if (r.startedAt) starts.push(Date.parse(r.startedAt));
    if (!r.completedAt) return null;
    latestEnd = Math.max(latestEnd, Date.parse(r.completedAt));
  }
  if (starts.length === 0) return null;
  return Math.max(0, latestEnd - Math.min(...starts));
}

/** Coze-style duration: sub-second to ms precision (0.632s / 0.000s), whole seconds (22s), then minutes (2m29s). */
export function formatDuration(ms: number): string {
  if (ms < 1000) return `${(ms / 1000).toFixed(3)}s`;
  if (ms < 60_000) return `${Math.round(ms / 1000)}s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.round((ms % 60_000) / 1000);
  return `${m}m${s}s`;
}

/** True when a value is worth showing — non-null and not an empty object. */
function hasRunContent(value: unknown): boolean {
  if (value === null || value === undefined) return false;
  if (typeof value === "object" && !Array.isArray(value)) return Object.keys(value).length > 0;
  return true;
}