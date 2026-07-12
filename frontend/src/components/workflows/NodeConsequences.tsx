import { Ic } from "@/_imported/ai-code-space/icons";

import { nodeBadges, type NodeBadge, type NodeBadgeSource } from "./nodeIcon";

/**
 * Plain-language "what happens when this runs" summary, shown in the inspector for a node that actually acts.
 * Same manifest flags that drive the card's Approval/Writes/Waits badges (via {@link nodeBadges}), but spelled
 * out as sentences at the surface where the author is about to configure or run the node — so consequences are
 * legible before the run, not decoded from pills. Renders nothing for a plain node, keeping the panel quiet.
 */
const COPY: Record<NodeBadge["kind"], { Icon: typeof Ic.Bell; text: string }> = {
  approval: { Icon: Ic.Bell, text: "Requires your approval before it acts." },
  write: { Icon: Ic.Zap, text: "Writes to external systems — this runs for real." },
  wait: { Icon: Ic.Clock, text: "May pause the run and wait before continuing." },
};

export function NodeConsequences({ source }: { source: NodeBadgeSource }) {
  const badges = nodeBadges(source);
  if (badges.length === 0) return null;

  return (
    <div className="wf-inspector-consequences">
      {badges.map((b) => {
        const { Icon, text } = COPY[b.kind];
        return (
          <div key={b.kind} className="wf-consequence" data-badge={b.kind}>
            <Icon size={13} />
            <span>{text}</span>
          </div>
        );
      })}
    </div>
  );
}
