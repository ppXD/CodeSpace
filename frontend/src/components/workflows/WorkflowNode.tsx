import { useCallback, useContext, useState } from "react";
import { Handle, NodeResizer, Position, useStore, type NodeProps, type ReactFlowState } from "@xyflow/react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { isAgentRunActive, type AgentRunStatus } from "@/api/agents";
import type { NodeKind, NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";
import { useAgentRun } from "@/hooks/use-agents";
import { ERROR_HANDLE } from "@/lib/workflowErrorRoute";

import { AgentRunTimeline } from "./AgentRunTimeline";
import { AgentToolCalls } from "./AgentToolCalls";
import { JsonView } from "./JsonView";
import { NodeRerunBadge } from "./NodeRerunBadge";
import { RerunMenu } from "./RerunMenu";
import { loopMinSize } from "./loopResize";
import { fanBranches, fanoutSummary, nodeIterationLabel } from "./mapBranches";
import { MapFanout } from "./MapFanout";
import { NodeAddContext, type NodeAddRequest } from "./nodeAddContext";
import { RunOpenContext } from "./runOpenContext";
import { CATCH_HANDLE, isContainerKind } from "./workflowContainers";

/**
 * Hover affordance on a node's right edge: click to open the "add a node here" picker (the new node
 * is auto-linked from this one). Drag-to-connect stays on the source Handle next to it. `nodrag`/`nopan`
 * stop React Flow from treating the click as a node-drag / canvas-pan.
 */
function AddNodeButton({ nodeId, onAddFrom }: { nodeId: string; onAddFrom: NodeAddRequest }) {
  return (
    <button
      type="button"
      className="wf-rf-add nodrag nopan"
      title="Add a node"
      onClick={(e) => { e.stopPropagation(); onAddFrom(nodeId, { x: e.clientX, y: e.clientY }); }}
    >
      <Ic.Plus size={13} />
    </button>
  );
}

/**
 * Custom React Flow node. Renders the icon + display name + the author's id label,
 * with a colored left bar that reflects NodeKind so triggers / terminals stand out.
 * Handles are added/removed based on Kind so the user can't accidentally wire
 * an incoming edge to a Trigger or an outgoing edge from a Terminal.
 *
 * <para>Output variables are intentionally NOT rendered on the node card — that pattern
 * bloats the canvas, makes auto-layout fight long output lists, and forces the card to
 * grow with no upper bound for nodes like <c>http.request</c>. Instead, clicking a node
 * opens the inspector where a "Provides" card lists every output with its reference path
 * (same surface Dify uses at the bottom of the right-side config panel).</para>
 */
export interface WorkflowNodeData extends Record<string, unknown> {
  nodeId: string;
  typeKey: string;
  displayName: string;
  iconKey: string | null;
  kind: NodeKind;
  /** Manifest-declared category ("AI", "Git", "Logic", …). Drives the icon fallback when iconKey is null. */
  category: string;
  label: string | null;
  /**
   * Manual Start node only: the workflow's declared input fields, rendered on the card so the
   * entry node shows its inputs the way Dify's Start node does. Unbounded output lists are kept
   * OFF the card (see above), but the input set on a manual trigger is small and is the node's
   * whole purpose, so it earns its place. Synced from the editor's workflow inputs state.
   */
  inputFields?: ReadonlyArray<{ name: string; label?: string | null; required?: boolean }>;
  /** A flow.loop container the user resized by its corner — explicit pixel size, persisted to the definition. Absent = auto-size to fit the body. */
  size?: { width: number; height: number };
  /**
   * When this card renders inside a run view (RunCanvas), the node's status in that run — drives the
   * status ring + corner badge. Absent in the editor, so the same component carries no overlay there.
   */
  runStatus?: NodeStatus;
  /**
   * Run view only: whether a from-node rerun would be ACCEPTED with this node as the target (the server's
   * `rerunnableFromHere` gate — no suspendable/container node in the closure, and every kept upstream cell settled
   * reusably). Drives whether the "Rerun from here" control renders — so a button that would 422 is never shown.
   * Absent in the editor.
   */
  rerunnableFromHere?: boolean;
  /**
   * When this card renders inside a run view: the run's row(s) for this node — one for a plain node, N
   * for a map/loop fan-out. Drives the coze-style result footer (status · duration, click to expand the
   * node's output / error). Absent in the editor, so no footer renders there.
   */
  runRows?: WorkflowRunNodeSummary[];
  /**
   * Run view only: a flow.map that COLLAPSED into a single fan-out card carries its worker body's branch rows
   * here (set by `runFanoutCollapse` in RunCanvas). Present ⇒ the node renders as a `FanoutNode` (an auto-sized
   * card embedding the activity-terminal fan-out) instead of a sized container framing a separate worker node.
   */
  fanout?: WorkflowRunNodeSummary[];
}

/**
 * Corner badge marking a node's status when the card is shown inside a run view. Pending stays
 * badge-less (the card is just dimmed); Running pulses; the rest carry a single glyph in the shared
 * status tone (matching RunStatusBadge across the app).
 */
function RunStatusDot({ status }: { status: NodeStatus }) {
  if (status === "Pending") return null;
  const glyph =
    status === "Success" ? <Ic.Check size={11} />
    : status === "Failure" ? <Ic.X size={11} />
    : status === "Suspended" ? <Ic.Pause size={11} />
    : status === "Skipped" ? <Ic.Dot size={13} />
    : <span className="wf-rf-status-spin" />;
  return <span className="wf-rf-status-badge" data-status={status.toLowerCase()} aria-hidden="true">{glyph}</span>;
}

/**
 * Coze/Dify-style run-result footer that hangs UNDER a node in a run view: a compact "status · duration"
 * bar that expands in place to reveal the node's output (and error). The graph flows left→right, so the
 * space below each node is free — the bar floats there without overlapping neighbours. Renders only when
 * the node has run rows (run view); the editor card never carries it.
 */
function RunResultBar({ status, rows, title }: { status: NodeStatus; rows: WorkflowRunNodeSummary[]; title?: string }) {
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
        <span className="wf-rf-result-label">{resultLabel(status, rows)}</span>
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
 * One run row's detail inside the result panel — error first, then output, then input. For an `agent.code`
 * step it also embeds the LIVE agent run (status + event timeline + governed tool-call audit), so you watch
 * the agent work without leaving the canvas; for a `flow.subworkflow` step it offers to open the child run.
 * Falls back to "no output" only when the row carries nothing at all.
 */
function RunRowDetail({ row }: { row: WorkflowRunNodeSummary }) {
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

/**
 * The run-result footer under a node. A flow.map fan-out with ≥2 branches renders the activity-terminal
 * {@link MapFanout} — a summary line + a per-branch status dot strip + one focused branch terminal — so a K-branch
 * map reads as one card, not K stacked rows. Everything else (a plain node, a SINGLE-branch map, a loop / try
 * fan-out) keeps the coze-style {@link RunResultBar}, whose single-row path embeds the agent run richly. Both hang
 * in the free space below the node.
 */
function NodeRunFooter({ status, rows, title }: { status: NodeStatus; rows: WorkflowRunNodeSummary[]; title?: string }) {
  if (fanBranches(rows).length >= 2) return <MapFanout rows={rows} renderBranch={(row) => <RunRowDetail row={row} />} />;
  return <RunResultBar status={status} rows={rows} title={title} />;
}

/** A row earns the expand caret when it carries anything inspectable — output, input, error, an agent run, or a child run. */
function isRowExpandable(row: WorkflowRunNodeSummary): boolean {
  return hasRunContent(row.outputs) || !!row.error || hasRunContent(row.inputs) || !!row.agentRunId || !!row.childRunId;
}

/** The status glyph in the result bar — mirrors the corner badge's tone vocabulary. */
function resultGlyph(status: NodeStatus) {
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
function aggregateDurationMs(rows: WorkflowRunNodeSummary[]): number | null {
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
function formatDuration(ms: number): string {
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

/**
 * A container's NodeResizer minimum size, derived from the LIVE React Flow store: the bounding box of
 * its body nodes (loopMinSize) — recomputed as body nodes are added / moved / resized, so a corner-drag
 * can shrink the box only down to the items inside it, never clipping past their borders.
 */
function useContainerMinSize(containerId: string) {
  return useStore(
    useCallback(
      (s: ReactFlowState) => loopMinSize([...s.nodeLookup.values()].filter((n) => n.parentId === containerId)),
      [containerId],
    ),
    (a, b) => a.minWidth === b.minWidth && a.minHeight === b.minHeight,
  );
}

/**
 * A container node (flow.loop / flow.try): the React Flow node's style sets its size and its body
 * subgraph (child nodes with parentId === this node) renders INSIDE via React Flow's parent/child
 * positioning. We draw only the frame + header so the body shows through. Corner-drag resizes the box;
 * the resizer's minimum is the body's bounding box (useContainerMinSize), so it can't shrink past the
 * items inside.
 *
 * The bottom handle differs by kind: a LOOP can fail (its body failure with no error edge), so it
 * exposes the universal `error` handle; a TRY never fails (it catches), so it exposes the `catch`
 * handle instead — the run routes there when a body node fails unhandled.
 *
 * Split into its own component so the store subscription (useContainerMinSize) mounts only for
 * container nodes, not for every node on the canvas.
 */
function ContainerNode({ id, d, selected }: { id: string; d: WorkflowNodeData; selected: boolean | undefined }) {
  const { minWidth, minHeight } = useContainerMinSize(id);
  const onAddFrom = useContext(NodeAddContext);
  const isTry = d.kind === "Try";
  return (
    <div className="wf-rf-loop" data-kind={d.kind.toLowerCase()} data-selected={selected} data-run-status={d.runStatus}>
      {d.runStatus && <RunStatusDot status={d.runStatus} />}
      {/* Drag a corner/edge to resize. Min size = the body's bounding box, so the box never shrinks
          past its items; the new size is persisted to the definition via the editor's onNodesChange. */}
      <NodeResizer isVisible={selected} minWidth={minWidth} minHeight={minHeight} lineClassName="wf-rf-resize-line" handleClassName="wf-rf-resize-handle" />
      <Handle type="target" position={Position.Left} className="wf-rf-handle" />
      <div className="wf-rf-loop-head">
        <span className="wf-rf-loop-icon">{iconFor(d)}</span>
        <span className="wf-rf-loop-title">{d.label ?? d.displayName}</span>
        <span className="wf-rf-loop-ref">{d.nodeId}</span>
        <NodeRerunBadge nodeId={d.nodeId} className="wf-rf-loop-rerun" />
      </div>
      <Handle type="source" position={Position.Right} className="wf-rf-handle" />
      {isTry ? (
        <Handle id={CATCH_HANDLE} type="source" position={Position.Bottom} className="wf-rf-handle wf-rf-handle-catch" title="On caught failure → connect to a handler node" />
      ) : (
        <Handle id={ERROR_HANDLE} type="source" position={Position.Bottom} className="wf-rf-handle wf-rf-handle-error" title="On error → connect to a handler node" />
      )}
      {onAddFrom && <AddNodeButton nodeId={d.nodeId} onAddFrom={onAddFrom} />}
      {d.runStatus && d.runRows && <NodeRunFooter status={d.runStatus} rows={d.runRows} />}
    </div>
  );
}

/**
 * A flow.map COLLAPSED to one fan-out card (run view, set by `runFanoutCollapse`) — a regular auto-sized node (no
 * fixed container size) whose body IS the activity-terminal {@link MapFanout} (summary + per-branch dot strip + one
 * focused branch terminal). It replaces the container frame + a separate worker box + dangling intra-edges with a
 * single card that reads like the Activity tab; being a normal node (not a child clipped by a container box), its
 * branch dots expand the terminal in place.
 */
function FanoutNode({ d, selected }: { d: WorkflowNodeData; selected: boolean | undefined }) {
  return (
    <div className="wf-rf-node wf-rf-fanout-node" data-kind={d.kind.toLowerCase()} data-selected={selected} data-run-status={d.runStatus}>
      {d.runStatus && <RunStatusDot status={d.runStatus} />}
      <Handle type="target" position={Position.Left} className="wf-rf-handle" />
      <div className="wf-rf-fanout-node-head">
        <span className="wf-rf-node-icon">{iconFor(d)}</span>
        <span className="wf-rf-fanout-node-title">{d.label ?? d.displayName}</span>
        <span className="wf-rf-node-ref">{d.nodeId}</span>
        <NodeRerunBadge nodeId={d.nodeId} className="wf-rf-fanout-node-rerun" />
      </div>
      <Handle type="source" position={Position.Right} className="wf-rf-handle" />
      <MapFanout rows={d.fanout ?? []} renderBranch={(row) => <RunRowDetail row={row} />} inline />
    </div>
  );
}

export function WorkflowNode({ id, data, selected }: NodeProps) {
  const d = data as WorkflowNodeData;
  const fields = d.inputFields ?? [];
  // null outside the editor (no provider) → no "+" button. A Terminal has no output, so never add from it.
  const onAddFrom = useContext(NodeAddContext);
  const showAdd = d.kind !== "Terminal";

  // An agent.code node parks (Suspended) the whole time its agent works; surface the agent's LIVE status so
  // the card reads as active, not an idle wait — matching the timeline view. Single agent row only; the hook
  // stays unconditional (disabled, no fetch, when there's no agent row — the editor, a plain node, a fan-out).
  const agentRow = d.runRows?.length === 1 ? d.runRows[0] : undefined;
  const agentRun = useAgentRun(agentRow?.agentRunId ?? undefined);
  const runStatus = effectiveRunStatus(d.runStatus, d.runRows, agentRun.data?.status);
  const parkedTitle = runStatus !== d.runStatus ? "Parked (Suspended) while its agent runs" : undefined;

  // A flow.map collapsed to a fan-out card (run view) renders as a self-contained card — checked BEFORE the
  // container branch so its Map kind doesn't route it to ContainerNode.
  if (d.fanout) return <FanoutNode d={d} selected={selected} />;

  // A container (loop / try / map) draws only its frame + header; its body renders inside. It's its own
  // component so the store subscription it needs (for the resize-min) doesn't run for every node.
  if (isContainerKind(d.kind)) return <ContainerNode id={id} d={d} selected={selected} />;

  // A loop/map body's entry marker (flow.loop_start / flow.map_start) is source-only: the engine seeds
  // it at the start of every iteration / branch, so it can't have an incoming edge, and a passthrough
  // never fails, so it must not expose an error handle either (wiring the body off that dead handle
  // skips it every pass). (flow.try_start keeps the default handles — Try's body entry differs.)
  const isLoopStart = d.typeKey === "flow.loop_start" || d.typeKey === "flow.map_start";

  return (
    <div className="wf-rf-node" data-kind={d.kind.toLowerCase()} data-selected={selected} data-run-status={runStatus}>
      {runStatus && <RunStatusDot status={runStatus} />}
      {d.kind !== "Trigger" && !isLoopStart && <Handle type="target" position={Position.Left} className="wf-rf-handle" />}
      <div className="wf-rf-node-bar" />
      <div className="wf-rf-node-icon">{iconFor(d)}</div>
      <div className="wf-rf-node-body">
        {/* Lead with the human name (the editable Label, falling back to the node type's display name);
            the node id is the immutable reference key (used in {{nodes.<id>.outputs.…}}), shown as a
            muted sub-line so it's visible to copy but no longer dominates the card. */}
        <div className="wf-rf-node-title">{d.label ?? d.displayName}</div>
        <div className="wf-rf-node-ref">{d.nodeId}</div>
        <NodeRerunBadge nodeId={d.nodeId} className="wf-rf-node-rerun" />
        {fields.length > 0 && (
          <ul className="wf-rf-node-fields">
            {fields.map((f) => (
              <li key={f.name} className="wf-rf-node-field">
                <span className="wf-rf-node-field-name">{f.name}</span>
                {/* label + required form one right-aligned meta group, so the variable-name
                    column stays flush-left and `required` flush-right regardless of how the
                    names/labels differ in length (no ragged middle). */}
                <span className="wf-rf-node-field-meta">
                  {f.label && <span className="wf-rf-node-field-label">{f.label}</span>}
                  {f.required && <span className="wf-rf-node-field-req">required</span>}
                </span>
              </li>
            ))}
          </ul>
        )}
        {runStatus === "Failure" && d.rerunnableFromHere && <RerunMenu target={{ kind: "node", nodeId: id }} className="wf-rerun-node-row nodrag nopan" />}
      </div>
      {d.kind !== "Terminal" && <Handle type="source" position={Position.Right} className="wf-rf-handle" />}
      {/* Error output — connect it to a handler node to catch this node's failure (route the run
          there instead of failing it). Only meaningful on regular nodes that can fail-and-continue.
          Sits on the BOTTOM edge (the right edge is the node's normal left→right output). */}
      {d.kind === "Regular" && !isLoopStart && (
        <Handle
          id={ERROR_HANDLE}
          type="source"
          position={Position.Bottom}
          className="wf-rf-handle wf-rf-handle-error"
          title="On error → connect to a handler node"
        />
      )}
      {showAdd && onAddFrom && <AddNodeButton nodeId={d.nodeId} onAddFrom={onAddFrom} />}
      {runStatus && d.runRows && <NodeRunFooter status={runStatus} rows={d.runRows} title={parkedTitle} />}
    </div>
  );
}

/**
 * The status to PAINT for a node. An agent.code node parks (Suspended) while its agent run is still
 * working, so — for the single-agent-row case — surface the agent's live activity as "Running" instead of a
 * misleading idle "Suspended" (mirrors the timeline view's isParkedOnLiveAgent). Everything else is the raw
 * node status.
 */
function effectiveRunStatus(runStatus: NodeStatus | undefined, rows: WorkflowRunNodeSummary[] | undefined, agentStatus: AgentRunStatus | undefined): NodeStatus | undefined {
  if (runStatus === "Suspended" && rows?.length === 1 && !!rows[0].agentRunId && isAgentRunActive(agentStatus)) {
    return "Running";
  }
  return runStatus;
}

/** Manifest iconKey → icon. The author's hint wins; misses fall back to Kind, then category. */
const ICON_BY_KEY: Record<string, typeof Ic.Box> = {
  "git-pull-request": Ic.PrOpen,
  "git-commit-horizontal": Ic.Branch,
  "file-diff": Ic.Branch,
  "message-square": Ic.Chat,
  sparkles: Ic.Sparkles,
  "circle-stop": Ic.CircleStop,
  zap: Ic.Zap,
  play: Ic.Play,
  workflow: Ic.Workflow,
};

function iconFor(d: WorkflowNodeData) {
  // The manifest's iconKey hint wins; otherwise infer from Kind, then the declared category.
  const Icon =
    (d.iconKey ? ICON_BY_KEY[d.iconKey] : undefined)
    ?? (d.kind === "Trigger" ? Ic.Zap
      : d.kind === "Terminal" ? Ic.CircleStop
      : d.kind === "Map" ? Ic.Fork
      : d.category === "AI" ? Ic.Sparkles
      : d.category === "Git" ? Ic.Branch
      : Ic.Box);

  return <Icon size={13} />;
}
