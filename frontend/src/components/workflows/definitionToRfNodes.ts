import type { Node } from "@xyflow/react";

import type { NodeManifestDto, WorkflowDefinition } from "@/api/workflows";

import type { WorkflowNodeData } from "./WorkflowNode";

// Default size of a flow.loop container on the canvas — roomy enough for a small body row. The
// body's nodes render inside it (React Flow parent/child); the user resizes/repositions as needed.
export const LOOP_CONTAINER_W = 820;
export const LOOP_CONTAINER_H = 240;

/**
 * Map a saved workflow definition into React Flow nodes (the canvas's load path).
 *
 * z-index contract (works at ANY nesting depth, including a loop inside a loop): a node's zIndex is
 * its nesting depth — a top-level loop container sits at 0, its body at 1, a nested loop container at
 * 1, the nested body at 2, … so a child's handles always paint ABOVE its container and stay
 * clickable/connectable. (Top-level non-loop nodes keep no explicit zIndex = default stacking.) Nodes
 * are emitted parent-before-child at every level so React Flow's parent/child positioning resolves.
 * The editor also disables React Flow's elevate-on-select so a selected container can't jump above
 * its own children.
 */
export function definitionToRfNodes(
  def: WorkflowDefinition,
  manifestByType: Map<string, NodeManifestDto>
): Node<WorkflowNodeData>[] {
  // Auto-layout: left→right row, ~300px column spacing (a loop container is wider), when position
  // is missing. Workflows are typically <20 nodes and the user immediately fixes positions on first
  // drag, so no real layout engine is needed. Horizontal mirrors the Dify-style left→right flow.
  let fallbackX = 80;

  // Nesting depth = how many parentId hops to a top-level node (0 = top-level, 1 = loop body,
  // 2 = nested-loop body, …). Drives both the render order and the z-index.
  const byId = new Map(def.nodes.map((n) => [n.id, n]));
  const depthOf = (n: NodeDefinitionLike): number => {
    let depth = 0;
    let parent = n.parentId;
    while (parent) { depth++; parent = byId.get(parent)?.parentId; }
    return depth;
  };

  // A parent must appear BEFORE its children at EVERY level — sort by depth ascending (a nested loop
  // sits between its outer container and its own body).
  const ordered = [...def.nodes].sort((a, b) => depthOf(a) - depthOf(b));

  return ordered.map((n) => {
    const manifest = manifestByType.get(n.typeKey);
    const kind = manifest?.kind ?? "Regular";
    const isLoop = kind === "Loop";
    const depth = depthOf(n);

    const data: WorkflowNodeData = {
      nodeId: n.id,
      typeKey: n.typeKey,
      displayName: manifest?.displayName ?? n.typeKey,
      iconKey: manifest?.iconKey ?? null,
      kind,
      category: manifest?.category ?? "",
      label: n.label ?? null,
      // Manual start node shows the workflow's input fields on its card (Dify-style).
      ...(manifest?.isManual ? { inputFields: def.inputs ?? [] } : {}),
    };

    // A nested node renders INSIDE its container — position relative to the parent, zIndex = depth so
    // it (and its handles) paint above the container. A nested LOOP also needs the container size so it
    // renders as a box. No `extent: "parent"` so it can still be dragged back OUT.
    if (n.parentId) {
      const nested = { id: n.id, type: "wf", parentId: n.parentId, position: n.position ?? { x: 40, y: 90 }, data, zIndex: depth };
      return isLoop ? { ...nested, style: { width: LOOP_CONTAINER_W, height: LOOP_CONTAINER_H } } : nested;
    }

    const position = n.position ?? { x: fallbackX, y: 80 };
    if (!n.position) fallbackX += isLoop ? LOOP_CONTAINER_W + 80 : 320;

    // A top-level loop container is sized so its body fits + sits at zIndex 0 (below its body);
    // everything else top-level is a normal card with default stacking.
    return isLoop
      ? { id: n.id, type: "wf", position, data, style: { width: LOOP_CONTAINER_W, height: LOOP_CONTAINER_H }, zIndex: 0 }
      : { id: n.id, type: "wf", position, data };
  });
}

/** The shape depthOf needs — just the parentId link. */
type NodeDefinitionLike = { parentId?: string | null };
