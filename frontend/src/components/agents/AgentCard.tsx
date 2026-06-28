import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentDefinitionSummary } from "@/api/agents";

import { type AgentRole, deriveRole } from "./agentRole";

/**
 * One agent on the bench — a role-tinted card that reads as a schedulable unit: identity (avatar + role badge +
 * name + @handle + description), loadout (model / autonomy / tools chips + skill tokens), and a recent-performance
 * micro-row. The role is a display-only heuristic (see deriveRole). Per-agent performance isn't aggregated yet, so
 * the micro-row shows "No recent runs" until that backend slice lands. Click / Enter opens the detail.
 */
export function AgentCard({ agent, onOpen }: { agent: AgentDefinitionSummary; onOpen: () => void }) {
  const role = deriveRole(agent);
  const meta = ROLE_META[role];
  const Avatar = meta.Icon;

  return (
    <article
      className="ab-card"
      tabIndex={0}
      aria-label={`Open ${agent.name}`}
      onClick={onOpen}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(); } }}
    >
      <div className="ab-top">
        <div className="ab-avatar" style={{ background: meta.bg, color: meta.fg }}><Avatar size={18} /></div>
        <div className="ab-id">
          <div className="ab-namerow">
            <span className="ab-name">{agent.name}</span>
            <span className="ab-handle">@{agent.slug}</span>
            <span className="ab-role" style={{ background: meta.bg, color: meta.fg }}>{meta.label}</span>
          </div>
          {agent.description && <div className="ab-desc" title={agent.description}>{agent.description}</div>}
        </div>
        <div className="ab-origin">
          {agent.origin === "Imported"
            ? <><Ic.Box size={12} /> Imported</>
            : <><span className="ab-dot" style={{ background: "var(--good)" }} /> Authored</>}
        </div>
      </div>

      <div className="ab-chips">
        <span className="ab-chip"><Ic.Sparkles size={12} /> {agent.model ?? "Auto"}</span>
        <span className="ab-chip"><Ic.Zap size={12} /> {agent.defaultAutonomy ?? "Standard"}</span>
        <span className="ab-chip"><Ic.Wrench size={12} /> {toolsLabel(agent.tools)}</span>
        <SkillTokens skills={agent.boundSkills} />
      </div>

      <div className="ab-perf ab-perf-empty">
        <span className="ab-dot" style={{ background: "var(--muted-2)" }} /> No recent runs
      </div>
    </article>
  );
}

/** Bound-skill tokens (the AgentSkillBinding join), capped so a heavy persona doesn't blow out the row; empty → a muted hint. */
function SkillTokens({ skills }: { skills: AgentDefinitionSummary["boundSkills"] }) {
  if (!skills || skills.length === 0) return <span className="ab-chip ab-chip-muted"><Ic.Puzzle size={12} /> no skills yet</span>;

  const shown = skills.slice(0, 4);
  const extra = skills.length - shown.length;

  return (
    <>
      {shown.map((s) => <span key={s.skillDefinitionId} className="ab-tok" title={s.name}>@{s.slug}</span>)}
      {extra > 0 && <span className="ab-chip ab-chip-muted">+{extra}</span>}
    </>
  );
}

/** Tools tri-state matching the AgentDefinition null-vs-empty contract: null = harness default, [] = none, list = a count. */
function toolsLabel(tools: string[] | null): string {
  if (tools === null) return "Default tools";
  if (tools.length === 0) return "No tools";

  return `${tools.length} ${tools.length === 1 ? "tool" : "tools"}`;
}

const ROLE_META: Record<AgentRole, { label: string; Icon: typeof Ic.Bot; bg: string; fg: string }> = {
  Architect: { label: "Architect", Icon: Ic.Compass, bg: "var(--accent-soft)", fg: "var(--accent)" },
  Reviewer: { label: "Reviewer", Icon: Ic.Shield, bg: "#E7F1EB", fg: "#3E7C5A" },
  Tracer: { label: "Tracer", Icon: Ic.Bug, bg: "#FBEAF0", fg: "#B0436A" },
  Planner: { label: "Planner", Icon: Ic.Map, bg: "#EEF1FB", fg: "#5C7CD6" },
  Generalist: { label: "Generalist", Icon: Ic.Bot, bg: "var(--panel-2)", fg: "var(--ink-2)" },
};
