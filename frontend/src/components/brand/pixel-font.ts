/**
 * The 5×8 bitmap the CodeSpace wordmark is drawn from.
 *
 * A real typeface can't produce this: the mark's whole identity is that every stroke sits on a
 * visible grid, which means the letterforms have to BE the grid rather than be rasterised onto one.
 * Rows 0–6 carry caps and ascenders, row 7 exists only so `p` can drop a descender.
 *
 * Only the glyphs the wordmark needs are here. Adding a letter means drawing it on the same grid —
 * there is no fallback, and a missing glyph throws rather than rendering a hole.
 */

const GLYPHS: Record<string, readonly string[]> = {
  C: [".###.", "#...#", "#....", "#....", "#....", "#...#", ".###.", "....."],
  o: [".....", ".....", ".###.", "#...#", "#...#", "#...#", ".###.", "....."],
  d: ["....#", "....#", ".####", "#...#", "#...#", "#...#", ".####", "....."],
  e: [".....", ".....", ".###.", "#...#", "#####", "#....", ".###.", "....."],
  S: [".###.", "#...#", "#....", ".###.", "....#", "#...#", ".###.", "....."],
  p: [".....", ".....", "####.", "#...#", "#...#", "####.", "#....", "#...."],
  a: [".....", ".....", ".###.", "....#", ".####", "#...#", ".####", "....."],
  c: [".....", ".....", ".###.", "#...#", "#....", "#...#", ".###.", "....."],
};

/** Glyph cell is 5 wide; the 6th column is the letter gap, so advance is 6. */
export const GLYPH_ADVANCE = 6;
export const GLYPH_HEIGHT = 8;

export interface PixelRun {
  x: number;
  y: number;
  width: number;
  /** Index of the glyph this run belongs to, so a caller can colour part of a word. */
  glyph: number;
}

/** Total width in grid units, trailing letter-gap trimmed. */
export function measure(text: string): number {
  return text.length * GLYPH_ADVANCE - 1;
}

/**
 * Horizontal runs of lit pixels, one rect each. Run-length encoding rather than a rect per pixel
 * cuts the element count by roughly two thirds — it matters because this renders inline in the
 * document, not as a fetched image.
 */
export function layout(text: string): PixelRun[] {
  const runs: PixelRun[] = [];

  text.split("").forEach((character, glyph) => {
    const rows = GLYPHS[character];

    if (!rows) throw new Error(`pixel-font has no glyph for '${character}'`);

    rows.forEach((row, y) => {
      let x = 0;

      while (x < row.length) {
        if (row[x] !== "#") { x += 1; continue; }

        let width = 0;
        while (x + width < row.length && row[x + width] === "#") width += 1;

        runs.push({ x: glyph * GLYPH_ADVANCE + x, y, width, glyph });
        x += width;
      }
    });
  });

  return runs;
}
