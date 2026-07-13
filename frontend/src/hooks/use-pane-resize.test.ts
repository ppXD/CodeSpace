import { describe, expect, it } from "vitest";

import { clampPaneWidth } from "./use-pane-resize";

/**
 * The drag interaction itself is exercised manually; the load-bearing, testable core is the clamp
 * that guarantees a rail can never be dragged below its minimum or past its maximum.
 */
describe("clampPaneWidth", () => {
  it("floors the palette at its minimum (keeps the tile grid 2-column-viable)", () => {
    expect(clampPaneWidth("palette", 0)).toBe(280);
    expect(clampPaneWidth("palette", 279)).toBe(280);
  });

  it("caps the palette at its maximum", () => {
    expect(clampPaneWidth("palette", 9999)).toBe(480);
  });

  it("keeps an in-range palette width, rounded to a whole px", () => {
    expect(clampPaneWidth("palette", 300.6)).toBe(301);
  });

  it("floors, caps, and passes through the inspector at its own bounds", () => {
    expect(clampPaneWidth("inspector", 100)).toBe(320);
    expect(clampPaneWidth("inspector", 5000)).toBe(680);
    expect(clampPaneWidth("inspector", 500)).toBe(500);
  });
});
