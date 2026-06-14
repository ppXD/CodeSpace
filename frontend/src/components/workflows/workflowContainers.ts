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
