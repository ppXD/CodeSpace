import { Handle, Position, type NodeProps } from "@xyflow/react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { NodeKind } from "@/api/workflows";

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
}

export function WorkflowNode({ data, selected }: NodeProps) {
  const d = data as WorkflowNodeData;
  return (
    <div className="wf-rf-node" data-kind={d.kind.toLowerCase()} data-selected={selected}>
      {d.kind !== "Trigger" && <Handle type="target" position={Position.Top} className="wf-rf-handle" />}
      <div className="wf-rf-node-bar" />
      <div className="wf-rf-node-icon">{iconFor(d)}</div>
      <div className="wf-rf-node-body">
        <div className="wf-rf-node-id">{d.nodeId}</div>
        <div className="wf-rf-node-type">{d.label ?? d.displayName}</div>
      </div>
      {d.kind !== "Terminal" && <Handle type="source" position={Position.Bottom} className="wf-rf-handle" />}
    </div>
  );
}

function iconFor(d: WorkflowNodeData) {
  // Honour the manifest's iconKey hint when present; otherwise infer from typeKey prefix.
  const key = d.iconKey ?? "";

  if (key === "git-pull-request") return <Ic.PrOpen size={13} />;
  if (key === "git-commit-horizontal") return <Ic.Branch size={13} />;
  if (key === "file-diff") return <Ic.Branch size={13} />;
  if (key === "message-square") return <Ic.Chat size={13} />;
  if (key === "sparkles") return <Ic.Sparkles size={13} />;
  if (key === "circle-stop") return <Ic.CircleStop size={13} />;
  if (key === "zap") return <Ic.Zap size={13} />;
  if (key === "play") return <Ic.Play size={13} />;

  if (d.kind === "Trigger") return <Ic.Zap size={13} />;
  if (d.kind === "Terminal") return <Ic.CircleStop size={13} />;
  // Category-based fallback — the manifest's declared category is the source of truth.
  if (d.category === "AI") return <Ic.Sparkles size={13} />;
  if (d.category === "Git") return <Ic.Branch size={13} />;

  return <Ic.Box size={13} />;
}
