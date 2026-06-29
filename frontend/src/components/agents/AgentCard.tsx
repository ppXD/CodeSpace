import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentDefinitionSummary } from "@/api/agents";

import { deriveRole } from "./agentRole";
import { toolsLabel } from "./agentRuntime";
import { RoleAvatar, RoleBadge } from "./roleBadge";

/**
 * One agent on the bench — a role-tinted card that reads like a character sheet: identity (avatar; the name with
 * its role badge alongside on line one — the name truncates so the badge keeps its place — then @handle on line
 * two), a one-line runtime spec (model · autonomy · tools), then a divider and the skills it carries as tokens.
 * The role is a display-only heuristic (see deriveRole). Click / Enter opens the editor.
 */
export function AgentCard({ agent, onOpen }: { agent: AgentDefinitionSummary; onOpen: () => void }) {
  const role = deriveRole(agent);

  return (
    <article
      className="ab-card"
      tabIndex={0}
      aria-label={`Edit ${agent.name}`}
      onClick={onOpen}
      onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); onOpen(); } }}
    >
      <div className="ab-top">
        <RoleAvatar role={role} />
        <div className="ab-id">
          <div className="ab-namerow">
            <span className="ab-name" title={agent.name}>{agent.name}</span>
            <RoleBadge role={role} />
          </div>
          <div className="ab-subrow">
            <span className="ab-handle">@{agent.slug}</span>
          </div>
          {agent.description && <div className="ab-desc" title={agent.description}>{agent.description}</div>}
        </div>
        <div className="ab-origin">
          {agent.origin === "Imported"
            ? <><Ic.Box size={12} /> <span className="ab-origin-pack" title={agent.packName ?? undefined}>{agent.packName ?? "Imported"}</span></>
            : <><span className="ab-dot" style={{ background: "var(--good)" }} /> Authored</>}
        </div>
      </div>

      <div className="ab-meta">
        <span className="ab-meta-item"><Ic.Sparkles size={12} /> {agent.model ?? "Auto"}</span>
        <span className="ab-meta-dot">·</span>
        <span className="ab-meta-item"><Ic.Zap size={12} /> {agent.defaultAutonomy ?? "Standard"}</span>
        <span className="ab-meta-dot">·</span>
        <span className="ab-meta-item"><Ic.Wrench size={12} /> {toolsLabel(agent.tools)}</span>
      </div>

      <div className="ab-divide" />

      <div className="ab-skills">
        <SkillTokens skills={agent.boundSkills} />
      </div>
    </article>
  );
}

/** Bound-skill tokens (the AgentSkillBinding join), capped so a heavy persona doesn't blow out the row; empty → a muted hint. */
function SkillTokens({ skills }: { skills: AgentDefinitionSummary["boundSkills"] }) {
  if (!skills || skills.length === 0) return <span className="ab-skills-empty"><Ic.Puzzle size={12} /> No skills bound</span>;

  const shown = skills.slice(0, 5);
  const extra = skills.length - shown.length;

  return (
    <>
      {shown.map((s) => <span key={s.skillDefinitionId} className="ab-tok" title={s.name}>@{s.slug}</span>)}
      {extra > 0 && <span className="ab-tok-more">+{extra}</span>}
    </>
  );
}
