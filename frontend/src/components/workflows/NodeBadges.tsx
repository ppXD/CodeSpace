import { nodeBadges, type NodeBadgeSource } from "./nodeIcon";

/**
 * Renders a node's "what this step does" badges (Approval / Writes / Waits) as small pills, driven purely by
 * manifest flags via {@link nodeBadges}. Returns null when the node has none, so the canvas and palette stay
 * quiet except where a step actually acts. Shared by the canvas card and the palette row so both read alike.
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
        <span key={b.kind} className="wf-badge" data-badge={b.kind}>{b.label}</span>
      ))}
    </span>
  );
}
