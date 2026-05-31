/** Default minimum loop-box size, used until the body's bounding box is known (empty / unmeasured body). */
export const LOOP_MIN_W = 320;
export const LOOP_MIN_H = 160;
/** Breathing room kept between the furthest body item's edge and the box edge — shrinking stops here. */
export const LOOP_RESIZE_PAD = 24;

/** The minimal geometry of a loop body node — position relative to the loop + its measured size. */
export interface BodyNodeGeom {
  position: { x: number; y: number };
  measured?: { width?: number; height?: number };
}

/**
 * The smallest a loop box may shrink to WITHOUT clipping its body: the furthest body-item edge (each
 * child's position relative to the loop + its measured size) + padding, floored at the default minimum.
 * A child that isn't measured yet contributes 0 (no constraint) until it renders; an empty body keeps
 * the default. Pure + deterministic → unit-tested; WorkflowNode feeds it the loop's live child nodes
 * from the React Flow store and hands the result to the NodeResizer so a corner-drag stops at the items.
 */
export function loopMinSize(children: readonly BodyNodeGeom[]): { minWidth: number; minHeight: number } {
  let minWidth = LOOP_MIN_W;
  let minHeight = LOOP_MIN_H;
  for (const n of children) {
    minWidth = Math.max(minWidth, n.position.x + (n.measured?.width ?? 0) + LOOP_RESIZE_PAD);
    minHeight = Math.max(minHeight, n.position.y + (n.measured?.height ?? 0) + LOOP_RESIZE_PAD);
  }
  return { minWidth, minHeight };
}
