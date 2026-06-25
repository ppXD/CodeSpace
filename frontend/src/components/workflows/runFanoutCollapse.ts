import type { Node } from "@xyflow/react";

import type { NodeManifestDto, WorkflowDefinition, WorkflowRunNodeSummary } from "@/api/workflows";

import { fanBranches } from "./mapBranches";
import type { WorkflowNodeData } from "./WorkflowNode";
import { isBodyStartTypeKey } from "./workflowContainers";

type DefNode = WorkflowDefinition["nodes"][number];

/**
 * For the RUN canvas only: which flow.map containers should COLLAPSE into a single fan-out card, and which
 * body nodes that hides. A map collapses when (a) its body is one worker node — a `flow.map_start` marker plus
 * exactly one real node, the common plan→map→synth / supervisor fan-out shape — and (b) it actually fanned out
 * (the worker has >= 2 element-branch rows, matching the footer's gate). The collapsed map renders as one
 * auto-sized fan-out card (the worker's branches) instead of a sized container framing a separate worker box
 * with dangling intra-edges. A multi-worker map body (a real subgraph) is NOT collapsed — it keeps its
 * container frame + inner nodes, since one card can't represent a multi-step body.
 */
export interface FanoutCollapse {
  /** map node id → the worker body's branch rows (what the fan-out card renders). */
  fanoutRowsByMapId: Map<string, WorkflowRunNodeSummary[]>;
  /** body node ids to hide on the canvas (the marker + the worker of every collapsed map). */
  hiddenNodeIds: Set<string>;
}

export function runFanoutCollapse(
  def: WorkflowDefinition,
  manifestByType: Map<string, NodeManifestDto>,
  rowsByNodeId: Map<string, WorkflowRunNodeSummary[]>,
): FanoutCollapse {
  const childrenOf = new Map<string, DefNode[]>();
  for (const n of def.nodes) {
    if (!n.parentId) continue;
    const arr = childrenOf.get(n.parentId);
    if (arr) arr.push(n); else childrenOf.set(n.parentId, [n]);
  }

  const fanoutRowsByMapId = new Map<string, WorkflowRunNodeSummary[]>();
  const hiddenNodeIds = new Set<string>();

  for (const node of def.nodes) {
    if (manifestByType.get(node.typeKey)?.kind !== "Map") continue;   // a flow.map container

    const children = childrenOf.get(node.id) ?? [];
    const workers = children.filter((c) => !isBodyStartTypeKey(c.typeKey));
    if (workers.length !== 1) continue;   // collapse only a single-worker body

    const rows = rowsByNodeId.get(workers[0].id) ?? [];
    if (fanBranches(rows).length < 2) continue;   // a real fan-out only (>= 2 branches, matching NodeRunFooter)

    fanoutRowsByMapId.set(node.id, rows);
    for (const c of children) hiddenNodeIds.add(c.id);   // the marker + the worker are now represented by the card
  }

  return { fanoutRowsByMapId, hiddenNodeIds };
}

/**
 * Rebuild a collapsed map's React Flow node: keep its STRUCTURE (id / type / position / parentId / extent) and
 * attach the run data (incl. `fanout`), dropping ONLY the container SIZING — `style` (so the card auto-sizes to the
 * fan-out and grows when a branch terminal expands) and, for a TOP-LEVEL map, the z-0 floor that kept the container
 * below its body. A map nested inside a loop/try KEEPS its `parentId` (so it stays in its container's coordinate
 * space, not flung to the canvas origin) and its depth `zIndex` (so it still paints above its container).
 */
export function collapsedMapNode(base: Node<WorkflowNodeData>, data: WorkflowNodeData): Node<WorkflowNodeData> {
  return { ...base, data, style: undefined, zIndex: base.parentId ? base.zIndex : undefined, draggable: false, selectable: false, connectable: false };
}
