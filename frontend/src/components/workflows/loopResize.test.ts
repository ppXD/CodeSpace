import { describe, expect, it } from "vitest";

import { LOOP_MIN_H, LOOP_MIN_W, LOOP_RESIZE_PAD, loopMinSize, type BodyNodeGeom } from "./loopResize";

const child = (x: number, y: number, width?: number, height?: number): BodyNodeGeom => ({
  position: { x, y },
  measured: width === undefined && height === undefined ? undefined : { width, height },
});

describe("loopMinSize", () => {
  it("keeps the default minimum for an empty body", () => {
    expect(loopMinSize([])).toEqual({ minWidth: LOOP_MIN_W, minHeight: LOOP_MIN_H });
  });

  it("keeps the default when the body fits well inside it", () => {
    // A small item near the top-left — its edge + pad is below the default floor.
    expect(loopMinSize([child(40, 40, 200, 60)])).toEqual({ minWidth: LOOP_MIN_W, minHeight: LOOP_MIN_H });
  });

  it("grows the minimum to the furthest item edge + padding", () => {
    // Item at x=300 w=280 → right edge 580 (+pad). y=200 h=120 → bottom 320 (+pad).
    const out = loopMinSize([child(300, 200, 280, 120)]);
    expect(out.minWidth).toBe(300 + 280 + LOOP_RESIZE_PAD);
    expect(out.minHeight).toBe(200 + 120 + LOOP_RESIZE_PAD);
  });

  it("takes the max across several body items (the bounding box, not the last)", () => {
    const out = loopMinSize([
      child(40, 40, 200, 60),    // small
      child(500, 80, 280, 96),   // furthest right
      child(60, 400, 200, 120),  // furthest down
    ]);
    expect(out.minWidth).toBe(500 + 280 + LOOP_RESIZE_PAD);
    expect(out.minHeight).toBe(400 + 120 + LOOP_RESIZE_PAD);
  });

  it("treats an unmeasured item as zero-size (no over-constraint until it renders)", () => {
    // No measured dims → contributes only its position; well under the default → default holds.
    expect(loopMinSize([child(40, 90)])).toEqual({ minWidth: LOOP_MIN_W, minHeight: LOOP_MIN_H });
  });

  it("constrains only the axis that overflows", () => {
    // Wide item (overflows width) but shallow (height stays default).
    const out = loopMinSize([child(400, 40, 400, 50)]);
    expect(out.minWidth).toBe(400 + 400 + LOOP_RESIZE_PAD);
    expect(out.minHeight).toBe(LOOP_MIN_H);
  });
});
