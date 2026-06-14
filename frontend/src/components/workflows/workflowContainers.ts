import type { NodeKind } from "@/api/workflows";

/**
 * The catch output handle of a flow.try scope (mirrors backend WorkflowHandles.Catch). An edge from
 * this handle is the try's failure branch: when a body node fails unhandled, the engine routes the run
 * down it (the failure becomes the try's `error` output) instead of failing the run. Unlike the error
 * handle it's a normal branch handle, so a try may have more than one catch edge.
 */
export const CATCH_HANDLE = "catch";

/**
 * A container kind owns a body subgraph that renders INSIDE it as a box on the canvas (React Flow
 * parent/child). flow.loop, flow.try and flow.map are containers; they share the canvas affordances
 * (sized box, drag a step in/out, auto-fit, nested) — only their output handles + config differ.
 */
export function isContainerKind(kind: NodeKind | string | null | undefined): boolean {
  return kind === "Loop" || kind === "Try" || kind === "Map";
}

/**
 * The body-entry marker typeKey auto-created inside a container (the single root of its body subgraph):
 * flow.loop → flow.loop_start, flow.try → flow.try_start, flow.map → flow.map_start. Null for a
 * non-container typeKey.
 */
export function bodyStartTypeKey(containerTypeKey: string): string | null {
  if (containerTypeKey === "flow.loop") return "flow.loop_start";
  if (containerTypeKey === "flow.try") return "flow.try_start";
  if (containerTypeKey === "flow.map") return "flow.map_start";
  return null;
}

/**
 * True for a container's body-entry marker (flow.loop_start / flow.try_start / flow.map_start). These
 * are locked inside their container (extent:"parent") and never hand-placed from the palette / never
 * orphaned on drag.
 */
export function isBodyStartTypeKey(typeKey: string | undefined): boolean {
  return typeKey === "flow.loop_start" || typeKey === "flow.try_start" || typeKey === "flow.map_start";
}

/**
 * Two nodes share a container scope iff they have the SAME container owner — both top-level, or both
 * inside the SAME container body. This is the exact save-time rule the backend enforces in
 * DefinitionValidator.CheckNoEdgeCrossesContainerBoundary: `ownerByNodeId[from] !== ownerByNodeId[to]`
 * is a crossing edge (the engine's SubgraphView silently DROPS such an edge, so it would never fire).
 *
 * The container-boundary exceptions the backend documents fall out of this single rule without special
 * casing, because they're all SAME-owner edges:
 *   - container node ↔ an outside sibling: the container node's own owner equals its siblings' owner
 *     (a top-level map has parent null, same as the top-level node it wires to) — same scope, allowed.
 *   - a body-start (flow.map_start) ↔ another body node: both are parented to the container — same
 *     scope, allowed.
 *   - an in-body `error` edge: source + target are both body nodes — same scope, allowed.
 *   - a try's `catch` edge: it's sourced from the try NODE at the parent level, so source + target
 *     share the parent's owner — same scope, allowed.
 *
 * `ownerById` maps every node id to its container owner id (its React Flow `parentId`), with `null`/
 * absent meaning top-level. A missing endpoint (unknown id) is treated as "can't tell" → allowed here,
 * mirroring the backend's `continue` for an unknown endpoint (CheckEdgeEndpoints flags it separately).
 */
export function sameContainerScope(ownerById: ReadonlyMap<string, string | null | undefined>, fromId: string, toId: string): boolean {
  if (!ownerById.has(fromId) || !ownerById.has(toId)) return true;

  return (ownerById.get(fromId) ?? null) === (ownerById.get(toId) ?? null);
}
