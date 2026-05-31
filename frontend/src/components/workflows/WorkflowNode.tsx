import { Handle, Position, type NodeProps } from "@xyflow/react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { NodeKind } from "@/api/workflows";
import { ERROR_HANDLE } from "@/lib/workflowErrorRoute";

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
}

export function WorkflowNode({ data, selected }: NodeProps) {
  const d = data as WorkflowNodeData;
  const fields = d.inputFields ?? [];

  // A loop container: the React Flow node's style sets its size and its body subgraph (child nodes
  // with parentId === this loop) renders INSIDE via React Flow's parent/child positioning. We draw
  // only the frame + header so the body shows through. Handles carry the run into/out of the whole
  // loop and route a loop failure onward (its own `error` edge).
  if (d.kind === "Loop") {
    return (
      <div className="wf-rf-loop" data-selected={selected}>
        <Handle type="target" position={Position.Left} className="wf-rf-handle" />
        <div className="wf-rf-loop-head">
          <span className="wf-rf-loop-icon">{iconFor(d)}</span>
          <span className="wf-rf-loop-id">{d.nodeId}</span>
          <span className="wf-rf-loop-type">{d.label ?? d.displayName}</span>
        </div>
        <Handle type="source" position={Position.Right} className="wf-rf-handle" />
        <Handle id={ERROR_HANDLE} type="source" position={Position.Bottom} className="wf-rf-handle wf-rf-handle-error" title="On error → connect to a handler node" />
      </div>
    );
  }

  // The loop body's entry marker (flow.loop_start) is source-only: the engine seeds it at the start
  // of every iteration, so it can't have an incoming edge, and a passthrough never fails, so it must
  // not expose an error handle either (wiring the body off that dead handle skips it every pass).
  const isLoopStart = d.typeKey === "flow.loop_start";

  return (
    <div className="wf-rf-node" data-kind={d.kind.toLowerCase()} data-selected={selected}>
      {d.kind !== "Trigger" && !isLoopStart && <Handle type="target" position={Position.Left} className="wf-rf-handle" />}
      <div className="wf-rf-node-bar" />
      <div className="wf-rf-node-icon">{iconFor(d)}</div>
      <div className="wf-rf-node-body">
        <div className="wf-rf-node-id">{d.nodeId}</div>
        <div className="wf-rf-node-type">{d.label ?? d.displayName}</div>
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
    </div>
  );
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
      : d.category === "AI" ? Ic.Sparkles
      : d.category === "Git" ? Ic.Branch
      : Ic.Box);

  return <Icon size={13} />;
}
