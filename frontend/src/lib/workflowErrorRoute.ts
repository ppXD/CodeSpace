import type { Edge } from "@xyflow/react";

/**
 * The reserved "error" source handle (mirrors backend WorkflowHandles.Error). An edge from this
 * handle is the node's failure branch: when the node fails, the engine routes the run down it
 * instead of failing the run. A node has at most one such edge.
 */
export const ERROR_HANDLE = "error";

/** The current error-route target of a node — the node its error edge points at, or null if none. */
export function errorRouteTarget(edges: Edge[], nodeId: string): string | null {
  const edge = edges.find((e) => e.source === nodeId && e.sourceHandle === ERROR_HANDLE);
  return edge ? edge.target : null;
}

/**
 * Set (or clear) a node's error route. Removes any existing error edge from the node, then —
 * when `targetId` is non-null — adds a fresh error edge to it. Normal edges are untouched.
 * Pure: returns a new array, mutates nothing.
 */
export function setErrorRoute(edges: Edge[], nodeId: string, targetId: string | null): Edge[] {
  const withoutErrorEdge = edges.filter((e) => !(e.source === nodeId && e.sourceHandle === ERROR_HANDLE));

  if (!targetId) return withoutErrorEdge;

  return [...withoutErrorEdge, buildErrorEdge(nodeId, targetId)];
}

/** Build the React Flow edge for an error route — distinct id + the red error-edge class. */
export function buildErrorEdge(nodeId: string, targetId: string): Edge {
  return {
    id: `e-error-${nodeId}-${targetId}`,
    source: nodeId,
    target: targetId,
    sourceHandle: ERROR_HANDLE,
    type: "smoothstep",
    animated: true,
    className: "wf-rf-edge-error",
  };
}
