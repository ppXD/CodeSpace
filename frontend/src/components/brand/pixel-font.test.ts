import { describe, expect, it } from "vitest";

import { GLYPH_ADVANCE, layout, measure } from "./pixel-font";

/**
 * The wordmark is drawn from this grid rather than set in a typeface, so nothing downstream would
 * catch a malformed glyph — a broken row renders as a subtly wrong letter, not as an error.
 */
describe("pixel-font", () => {
  it("keeps every run inside its own glyph cell", () => {
    // A run that overflows its 5-wide cell would collide with the next letter.
    layout("CodeSpace").forEach((run) => {
      const cellStart = run.glyph * GLYPH_ADVANCE;

      expect(run.x).toBeGreaterThanOrEqual(cellStart);
      expect(run.x + run.width).toBeLessThanOrEqual(cellStart + 5);
    });
  });

  it("assigns runs to the glyph they were drawn from", () => {
    const runs = layout("CodeSpace");

    expect(new Set(runs.map((run) => run.glyph))).toEqual(new Set([0, 1, 2, 3, 4, 5, 6, 7, 8]));
  });

  it("measures a word with the trailing letter gap trimmed", () => {
    expect(measure("CodeSpace")).toBe(9 * GLYPH_ADVANCE - 1);
  });

  it("refuses a character it has no glyph for", () => {
    // Silently skipping would render a word with a hole in it and look like a CSS bug.
    expect(() => layout("Code Space")).toThrow(/no glyph/);
  });
});
