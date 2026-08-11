/**
 * The CodeSpace mark: a blocky C with a cursor sitting in its opening.
 *
 * Three rectangles and a block, on the same grid as the wordmark. It is built this coarsely on
 * purpose — at a 16px favicon the mark has to survive being two shapes, and any counter, taper, or
 * rounded joint disappears at that size. The cursor is the only accent-coloured element in the
 * identity, which is what makes it read as a caret rather than as decoration.
 */
export function CodeSpaceMark({ size = 32, className }: { size?: number; className?: string }) {
  return (
    <svg
      className={className}
      viewBox="0 0 40 40"
      width={size}
      height={size}
      shapeRendering="crispEdges"
      role="img"
      aria-label="CodeSpace"
    >
      <title>CodeSpace</title>
      <rect x="4" y="4" width="32" height="7" fill="currentColor" />
      <rect x="4" y="11" width="7" height="18" fill="currentColor" />
      <rect x="4" y="29" width="32" height="7" fill="currentColor" />
      <rect x="27" y="17" width="9" height="6" fill="var(--auth-accent)" />
    </svg>
  );
}
