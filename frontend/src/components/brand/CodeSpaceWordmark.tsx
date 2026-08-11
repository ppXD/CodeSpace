import { GLYPH_HEIGHT, layout, measure } from "./pixel-font";

const TEXT = "CodeSpace";

/** "Code" is the product noun, "Space" takes the accent — the split is at the capital S. */
const ACCENT_FROM = 4;

const RUNS = layout(TEXT);
const WIDTH = measure(TEXT);

/**
 * The CodeSpace wordmark, drawn pixel by pixel on the grid in `pixel-font`.
 *
 * "Code" inherits `currentColor` so one component serves the dark brand panel and a light page
 * without a variant; "Space" is pinned to the accent, which is what keeps the two halves reading as
 * one word in two tones rather than as two words.
 */
export function CodeSpaceWordmark({ height = 40, className }: { height?: number; className?: string }) {
  return (
    <svg
      className={className}
      viewBox={`0 0 ${WIDTH} ${GLYPH_HEIGHT}`}
      width={(height * WIDTH) / GLYPH_HEIGHT}
      height={height}
      shapeRendering="crispEdges"
      role="img"
      aria-label={TEXT}
    >
      <title>{TEXT}</title>
      {RUNS.map((run) => (
        <rect
          key={`${run.x}-${run.y}`}
          x={run.x}
          y={run.y}
          width={run.width}
          height={1}
          fill={run.glyph < ACCENT_FROM ? "currentColor" : "var(--auth-accent)"}
        />
      ))}
    </svg>
  );
}
