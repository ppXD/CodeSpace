import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentDefinitionSummary } from "@/api/agents";

import { deriveRole } from "./agentRole";
import { autonomySummary, toolsLabel } from "./agentRuntime";
import { DrawerClose, DrawerFrame } from "./DrawerFrame";
import { RoleAvatar, RoleBadge } from "./roleBadge";

/**
 * The agent detail drawer's read view — a workbench overview of one persona in three sections: Identity (its
 * description + system prompt), Runtime (model / autonomy / tools + a plain-language permissions line), and
 * Capabilities (bound skills, pack origin, recent runs). "Edit" flips the drawer into the editor. Per-agent
 * performance isn't aggregated yet, so Recent runs shows a placeholder until that backend slice lands.
 */
export function AgentInspect({ agent, onEdit, onClose }: { agent: AgentDefinitionSummary; onEdit: () => void; onClose: () => void }) {
  const role = deriveRole(agent);

  const head = (
    <div className="mdl-head">
      <RoleAvatar role={role} size={38} />
      <div className="mdl-title-wrap">
        <div className="mdl-title" style={{ display: "flex", alignItems: "center", gap: 8 }}>{agent.name} <RoleBadge role={role} /></div>
        <div className="mdl-sub" style={{ fontFamily: "var(--font-mono, ui-monospace, monospace)" }}>@{agent.slug}</div>
      </div>
      <DrawerClose onClose={onClose} />
    </div>
  );

  const foot = (
    <div className="mdl-foot">
      <div />
      <button type="button" className="btn btn-primary" onClick={onEdit}><Ic.Edit size={14} /> Edit</button>
    </div>
  );

  return (
    <DrawerFrame label={agent.name} onClose={onClose} head={head} foot={foot}>
      <section className="drw-sec">
        <div className="drw-sec-h"><Ic.Bot size={13} /> Identity</div>
        <div className="drw-fld">Description</div>
        {agent.description ? <div className="drw-val">{agent.description}</div> : <div className="drw-val drw-muted">No description.</div>}
        <div className="drw-fld">System prompt</div>
        {agent.systemPrompt ? <pre className="drw-prompt">{agent.systemPrompt}</pre> : <div className="drw-val drw-muted">No system prompt.</div>}
      </section>

      <section className="drw-sec">
        <div className="drw-sec-h"><Ic.Sparkles size={13} /> Runtime</div>
        <div className="drw-chips">
          <span className="drw-chip"><Ic.Sparkles size={12} /> Model <b>{agent.model ?? "Auto"}</b></span>
          <span className="drw-chip"><Ic.Zap size={12} /> Autonomy <b>{agent.defaultAutonomy ?? "Standard"}</b></span>
          <span className="drw-chip"><Ic.Wrench size={12} /> Tools <b>{toolsLabel(agent.tools)}</b></span>
        </div>
        <div className="drw-perm"><Ic.Lock size={12} /> {autonomySummary(agent.defaultAutonomy)}</div>
      </section>

      <section className="drw-sec">
        <div className="drw-sec-h"><Ic.Puzzle size={13} /> Capabilities</div>
        <div className="drw-fld">Bound skills</div>
        {agent.boundSkills.length > 0
          ? <div className="drw-toks">{agent.boundSkills.map((s) => <span key={s.skillDefinitionId} className="drw-tok" title={s.name}>@{s.slug}</span>)}</div>
          : <div className="drw-val drw-muted">No skills bound. Skills are bound when importing a pack.</div>}
        <div className="drw-fld">Pack origin</div>
        <div className="drw-val" style={{ display: "flex", alignItems: "center", gap: 6 }}>
          {agent.origin === "Imported" ? <><Ic.Box size={13} /> Imported from a pack</> : <><Ic.Bot size={13} /> Authored in this team</>}
        </div>
        <div className="drw-fld">Recent runs</div>
        <div className="drw-val drw-muted">No recent runs yet — run this agent and its success rate + latency will appear here.</div>
      </section>
    </DrawerFrame>
  );
}
