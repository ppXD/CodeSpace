import { nodeBadges, type NodeBadgeSource } from "./nodeIcon";

/**
 * Renders a node's "what this step does" badges (Approval / Writes / Waits), driven purely by manifest flags
 * via {@link nodeBadges}. Returns null when the node has none, so the canvas and palette stay quiet except
 * where a step actually acts. The markup is one label span per badge; CSS decides the shape — the canvas card
 * shows full word pills, the palette row collapses each to a calm 7px semantic dot with the word carried by
 * the `title` tooltip (so the compact palette stays uniform without losing the meaning).
 *
 * (Its own file — a component can't live in nodeIcon.tsx alongside the icon/tone helper functions without
 * tripping react-refresh/only-export-components.)
 */
export function NodeBadges({ source }: { source: NodeBadgeSource }) {
  const badges = nodeBadges(source);
  if (badges.length === 0) return null;

  return (
    <span className="wf-badges">
      {badges.map((b) => (
        <span key={b.kind} className="wf-badge" data-badge={b.kind} title={b.label} aria-label={b.label}>{b.label}</span>
      ))}
    </span>
  );
}
