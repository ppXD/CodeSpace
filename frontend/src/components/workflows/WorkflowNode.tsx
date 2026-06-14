import { useCallback, useContext } from "react";
import { Handle, NodeResizer, Position, useStore, type NodeProps, type ReactFlowState } from "@xyflow/react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { NodeKind } from "@/api/workflows";
import { ERROR_HANDLE } from "@/lib/workflowErrorRoute";

import { loopMinSize } from "./loopResize";
import { NodeAddContext, type NodeAddRequest } from "./nodeAddContext";
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
    <div className="wf-rf-loop" data-kind={d.kind.toLowerCase()} data-selected={selected}>
      {/* Drag a corner/edge to resize. Min size = the body's bounding box, so the box never shrinks
          past its items; the new size is persisted to the definition via the editor's onNodesChange. */}
      <NodeResizer isVisible={selected} minWidth={minWidth} minHeight={minHeight} lineClassName="wf-rf-resize-line" handleClassName="wf-rf-resize-handle" />
      <Handle type="target" position={Position.Left} className="wf-rf-handle" />
      <div className="wf-rf-loop-head">
        <span className="wf-rf-loop-icon">{iconFor(d)}</span>
        <span className="wf-rf-loop-title">{d.label ?? d.displayName}</span>
        <span className="wf-rf-loop-ref">{d.nodeId}</span>
      </div>
      <Handle type="source" position={Position.Right} className="wf-rf-handle" />
      {isTry ? (
        <Handle id={CATCH_HANDLE} type="source" position={Position.Bottom} className="wf-rf-handle wf-rf-handle-catch" title="On caught failure → connect to a handler node" />
      ) : (
        <Handle id={ERROR_HANDLE} type="source" position={Position.Bottom} className="wf-rf-handle wf-rf-handle-error" title="On error → connect to a handler node" />
      )}
      {onAddFrom && <AddNodeButton nodeId={d.nodeId} onAddFrom={onAddFrom} />}
    </div>
  );
}

export function WorkflowNode({ id, data, selected }: NodeProps) {
  const d = data as WorkflowNodeData;
  const fields = d.inputFields ?? [];
  // null outside the editor (no provider) → no "+" button. A Terminal has no output, so never add from it.
  const onAddFrom = useContext(NodeAddContext);
  const showAdd = d.kind !== "Terminal";

  // A container (loop / try) draws only its frame + header; its body renders inside. It's its own
  // component so the store subscription it needs (for the resize-min) doesn't run for every node.
  if (isContainerKind(d.kind)) return <ContainerNode id={id} d={d} selected={selected} />;

  // A loop/map body's entry marker (flow.loop_start / flow.map_start) is source-only: the engine seeds
  // it at the start of every iteration / branch, so it can't have an incoming edge, and a passthrough
  // never fails, so it must not expose an error handle either (wiring the body off that dead handle
  // skips it every pass). (flow.try_start keeps the default handles — Try's body entry differs.)
  const isLoopStart = d.typeKey === "flow.loop_start" || d.typeKey === "flow.map_start";

  return (
    <div className="wf-rf-node" data-kind={d.kind.toLowerCase()} data-selected={selected}>
      {d.kind !== "Trigger" && !isLoopStart && <Handle type="target" position={Position.Left} className="wf-rf-handle" />}
      <div className="wf-rf-node-bar" />
      <div className="wf-rf-node-icon">{iconFor(d)}</div>
      <div className="wf-rf-node-body">
        {/* Lead with the human name (the editable Label, falling back to the node type's display name);
            the node id is the immutable reference key (used in {{nodes.<id>.outputs.…}}), shown as a
            muted sub-line so it's visible to copy but no longer dominates the card. */}
        <div className="wf-rf-node-title">{d.label ?? d.displayName}</div>
        <div className="wf-rf-node-ref">{d.nodeId}</div>
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
      {showAdd && onAddFrom && <AddNodeButton nodeId={d.nodeId} onAddFrom={onAddFrom} />}
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
      : d.kind === "Map" ? Ic.Fork
      : d.category === "AI" ? Ic.Sparkles
      : d.category === "Git" ? Ic.Branch
      : Ic.Box);

  return <Icon size={13} />;
}
