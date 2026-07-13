import { nodeBadges, type NodeBadgeSource } from "./nodeIcon";

/**
 * Renders a node's "what this step does" badges (Approval / Writes / Waits), driven purely by manifest flags
 * via {@link nodeBadges}. Returns null when the node has none, so the canvas and palette stay quiet except
 * where a step actually acts. The markup is one label span per badge; CSS decides the shape — the canvas card
 * shows full word pills, and the palette tile renders a smaller calm per-kind-tinted word tag centred at the
 * tile's foot (with a `title` tooltip), so the effect reads at a glance and every tile stays one height.
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
