import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentDefinitionSummary } from "@/api/agents";

import { deriveRole } from "./agentRole";
import { toolsLabel } from "./agentRuntime";
import { RoleAvatar, RoleBadge } from "./roleBadge";

/**
 * One agent on the bench — a role-tinted card that reads as a schedulable unit: identity (avatar + role badge +
 * name + @handle + description), loadout (model / autonomy / tools chips + skill tokens), and a recent-performance
 * micro-row. The role is a display-only heuristic (see deriveRole). Per-agent performance isn't aggregated yet, so
 * the micro-row shows "No recent runs" until that backend slice lands. Click / Enter opens the detail.
 */
export function AgentCard({ agent, onOpen }: { agent: AgentDefinitionSummary; onOpen: () => void }) {
  const role = deriveRole(agent);

  return (
    <article
      className="ab-card"
      tabIndex={0}
      aria-label={`Open ${agent.name}`}
      onClick={onOpen}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(); } }}
    >
      <div className="ab-top">
        <RoleAvatar role={role} />
        <div className="ab-id">
          <div className="ab-namerow">
            <span className="ab-name">{agent.name}</span>
            <span className="ab-handle">@{agent.slug}</span>
            <RoleBadge role={role} />
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
