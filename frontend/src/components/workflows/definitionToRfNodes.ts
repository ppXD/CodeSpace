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
 * z-index contract for the flow.loop container: the container sits at zIndex 0 and its body steps
 * at zIndex 1, so the steps (and their connect handles) always paint ABOVE the container's solid
 * body and stay clickable/connectable. Without this, a selected container can cover its own
 * children and swallow the pointer events you need to wire nodes inside the loop. The editor also
 * disables React Flow's elevate-on-select so a selected container can't jump above its children.
 */
export function definitionToRfNodes(
  def: WorkflowDefinition,
  manifestByType: Map<string, NodeManifestDto>
): Node<WorkflowNodeData>[] {
  // Auto-layout: vertical stack, 180px row spacing, when position is missing. Workflows
  // are typically <20 nodes and the user immediately fixes positions on first drag, so no
  // real layout engine is needed.
  let fallbackY = 80;

  // React Flow requires a parent (loop container) to appear BEFORE its children in the array.
  // Body nodes carry parentId; ordering top-level-first guarantees parent-before-child.
  const ordered = [...def.nodes].sort((a, b) => (a.parentId ? 1 : 0) - (b.parentId ? 1 : 0));

  return ordered.map((n) => {
    const manifest = manifestByType.get(n.typeKey);
    const kind = manifest?.kind ?? "Regular";

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

    // A loop body node renders INSIDE its container — position is relative to the parent. No
    // `extent: "parent"` so it can still be dragged back OUT (onNodeDragStop then un-nests it).
    // zIndex 1 keeps the step (and its handles) ABOVE the container's body so it stays clickable
    // and connectable; the container itself sits at zIndex 0 (below).
    if (n.parentId) {
      return { id: n.id, type: "wf", parentId: n.parentId, position: n.position ?? { x: 40, y: 60 }, data, zIndex: 1 };
    }

    const position = n.position ?? { x: 80, y: fallbackY };
    if (!n.position) fallbackY += kind === "Loop" ? 300 : 180;

    // A loop container is sized so its body subgraph fits; everything else is a normal card. It's
    // draggable from anywhere on the box — body steps render above it (parent/child z-order) so they
    // stay clickable and the box drags as one with its body.
    return kind === "Loop"
      ? { id: n.id, type: "wf", position, data, style: { width: LOOP_CONTAINER_W, height: LOOP_CONTAINER_H }, zIndex: 0 }
      : { id: n.id, type: "wf", position, data };
  });
}
