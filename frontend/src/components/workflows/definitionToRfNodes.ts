import type { Node } from "@xyflow/react";

import type { NodeManifestDto, WorkflowDefinition } from "@/api/workflows";

import { isBodyStartTypeKey, isContainerKind } from "./workflowContainers";
import type { WorkflowNodeData } from "./WorkflowNode";

// Default (minimum) size of a flow.loop container on the canvas — roomy enough for a small body row.
// The body's nodes render inside it (React Flow parent/child). A container auto-grows beyond this to
// fit its children (see fitLoopSizes), so a nested loop never overflows its parent.
export const LOOP_CONTAINER_W = 820;
export const LOOP_CONTAINER_H = 240;

// Estimated footprint of a non-container body node (real size is only known after render). Generous so
// a container doesn't clip a node; the dominant overlap case (a nested CONTAINER child) uses its EXACT
// computed size, not this estimate.
const NODE_EST_W = 280;
const NODE_EST_H = 96;
// Breathing room between the furthest child edge and the container edge.
const LOOP_FIT_PAD = 40;

/** Minimal node shape fitLoopSizes needs — id, parent link, top-left position, and whether it's a container (loop / try). */
export interface LoopFitItem {
  id: string;
  parentId?: string | null;
  x: number;
  y: number;
  isContainer: boolean;
}

/**
 * Auto-size every container (loop / try) to fit its children — so a nested container never overflows
 * its parent. Computed BOTTOM-UP (deepest first): a leaf container is at least the default size; an
 * outer one grows to wrap its inner container's already-computed size + padding. A non-container child
 * contributes a generous estimate (its real size isn't known until render); a container child
 * contributes its exact computed size. Pure + deterministic → unit-tested. Shared by the load path
 * (definitionToRfNodes) and the live re-fit on drag (the editor's onNodeDragStop).
 */
export function fitLoopSizes(items: LoopFitItem[]): Map<string, { width: number; height: number }> {
  const byId = new Map(items.map((i) => [i.id, i]));
  const depthOf = (i: LoopFitItem): number => {
    let depth = 0;
    let parent = i.parentId;
    while (parent) { depth++; parent = byId.get(parent)?.parentId; }
    return depth;
  };

  const childrenOf = new Map<string, LoopFitItem[]>();
  for (const i of items) if (i.parentId) (childrenOf.get(i.parentId) ?? childrenOf.set(i.parentId, []).get(i.parentId)!).push(i);

  const sizes = new Map<string, { width: number; height: number }>();
  // Deepest containers first, so an outer one sees its inner containers' sizes already settled.
  const containersDeepestFirst = items.filter((i) => i.isContainer).sort((a, b) => depthOf(b) - depthOf(a));

  for (const container of containersDeepestFirst) {
    let width = LOOP_CONTAINER_W;
    let height = LOOP_CONTAINER_H;
    for (const kid of childrenOf.get(container.id) ?? []) {
      const kw = kid.isContainer ? (sizes.get(kid.id)?.width ?? LOOP_CONTAINER_W) : NODE_EST_W;
      const kh = kid.isContainer ? (sizes.get(kid.id)?.height ?? LOOP_CONTAINER_H) : NODE_EST_H;
      width = Math.max(width, kid.x + kw + LOOP_FIT_PAD);
      height = Math.max(height, kid.y + kh + LOOP_FIT_PAD);
    }
    sizes.set(container.id, { width, height });
  }

  return sizes;
}

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

  // Auto-size each loop container to fit its body (so a nested loop never overflows its parent). A
  // node's position defaults to the same {40,90} a positionless body node gets below.
  const loopSizes = fitLoopSizes(def.nodes.map((n) => ({
    id: n.id,
    parentId: n.parentId,
    x: n.position?.x ?? 40,
    y: n.position?.y ?? 90,
    isContainer: isContainerKind(manifestByType.get(n.typeKey)?.kind ?? "Regular"),
  })));
  // An explicit (user-resized) size wins; otherwise auto-fit to the body; otherwise the default.
  const loopStyle = (n: { id: string; width?: number | null; height?: number | null }) =>
    n.width != null && n.height != null
      ? { width: n.width, height: n.height }
      : (loopSizes.get(n.id) ?? { width: LOOP_CONTAINER_W, height: LOOP_CONTAINER_H });

  return ordered.map((n) => {
    const manifest = manifestByType.get(n.typeKey);
    const kind = manifest?.kind ?? "Regular";
    const isContainer = isContainerKind(kind);
    const depth = depthOf(n);

    const data: WorkflowNodeData = {
      nodeId: n.id,
      typeKey: n.typeKey,
      displayName: manifest?.displayName ?? n.typeKey,
      iconKey: manifest?.iconKey ?? null,
      kind,
      category: manifest?.category ?? "",
      label: n.label ?? null,
      isSideEffecting: manifest?.isSideEffecting,
      canSuspend: manifest?.canSuspend,
      alwaysRequiresApproval: manifest?.alwaysRequiresApproval,
      outputs: manifest?.outputs,
      // Manual start node shows the workflow's input fields on its card (Dify-style).
      ...(manifest?.isManual ? { inputFields: def.inputs ?? [] } : {}),
      // An explicit (user-resized) container size — marks this container as "don't auto-size", and round-trips back out via rfToDefinition.
      ...(n.width != null && n.height != null ? { size: { width: n.width, height: n.height } } : {}),
    };

    // A nested node renders INSIDE its container — position relative to the parent, zIndex = depth so
    // it (and its handles) paint above the container. A nested CONTAINER also needs the container size
    // so it renders as a box. No `extent: "parent"` so it can still be dragged back OUT.
    if (n.parentId) {
      const nested = {
        id: n.id, type: "wf", parentId: n.parentId, position: n.position ?? { x: 40, y: 90 }, data, zIndex: depth,
        // A container's entry marker (loop_start / try_start) is LOCKED inside its container
        // (extent:"parent" clips it to the box), so it can never be dragged out / orphaned — the body
        // always has its root. Other body nodes stay free (no extent) so they can be dragged back out.
        ...(isBodyStartTypeKey(n.typeKey) ? { extent: "parent" as const } : {}),
      };
      return isContainer ? { ...nested, style: loopStyle(n) } : nested;
    }

    const position = n.position ?? { x: fallbackX, y: 80 };
    if (!n.position) fallbackX += isContainer ? LOOP_CONTAINER_W + 80 : 320;

    // A top-level container is sized so its body fits + sits at zIndex 0 (below its body);
    // everything else top-level is a normal card with default stacking.
    return isContainer
      ? { id: n.id, type: "wf", position, data, style: loopStyle(n), zIndex: 0 }
      : { id: n.id, type: "wf", position, data };
  });
}

/** The shape depthOf needs — just the parentId link. */
type NodeDefinitionLike = { parentId?: string | null };
