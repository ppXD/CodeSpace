import { Ic } from "@/_imported/ai-code-space/icons";

import { type AgentRole } from "./agentRole";

/**
 * Shared role identity — the avatar + badge for a derived agent role, used by both the bench card (in-page) and
 * the detail drawer (portaled outside .acs-root). The structural styles are inline so they render correctly in
 * either place. Each fg is a hand-darkened shade of its tint so the 11px badge text clears WCAG AA on the soft
 * background (raw var(--accent) on --accent-soft is only 2.5:1).
 */
const ROLE_META: Record<AgentRole, { label: string; Icon: typeof Ic.Bot; bg: string; fg: string }> = {
  Architect: { label: "Architect", Icon: Ic.Compass, bg: "var(--accent-soft)", fg: "#99452A" },
  Reviewer: { label: "Reviewer", Icon: Ic.Shield, bg: "#EAF4EE", fg: "#2D6A48" },
  Tracer: { label: "Tracer", Icon: Ic.Bug, bg: "#FBEAF0", fg: "#99365A" },
  Planner: { label: "Planner", Icon: Ic.Map, bg: "#EEF1FB", fg: "#2F5BA8" },
  Generalist: { label: "Generalist", Icon: Ic.Bot, bg: "var(--panel-2)", fg: "var(--ink-2)" },
};

/** Role-tinted avatar — a rounded square with the role's icon. */
export function RoleAvatar({ role, size = 40 }: { role: AgentRole; size?: number }) {
  const meta = ROLE_META[role];
  const Icon = meta.Icon;

  return (
    <div style={{ width: size, height: size, borderRadius: 11, display: "grid", placeItems: "center", background: meta.bg, color: meta.fg, flexShrink: 0 }}>
      <Icon size={Math.round(size * 0.46)} />
    </div>
  );
}

/** Role pill — the label on its tinted, AA-contrast background. */
export function RoleBadge({ role }: { role: AgentRole }) {
  const meta = ROLE_META[role];

  return <span style={{ flexShrink: 0, fontSize: 11, fontWeight: 500, padding: "1px 8px", borderRadius: 999, background: meta.bg, color: meta.fg }}>{meta.label}</span>;
}
