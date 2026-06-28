import { useState } from "react";

import { ApiError } from "@/api/request";
import { useAgentDefinition } from "@/hooks/use-agents";

import { AgentEditorForm } from "./AgentEditor";
import { AgentInspect } from "./AgentInspect";
import { EMPTY_FORM, formFromPersona } from "./agentForm";
import { DrawerClose, DrawerFrame } from "./DrawerFrame";

/**
 * Agent detail drawer — the right-side workbench for a persona, replacing the old centered editor modal. Create
 * opens straight into the editor; clicking a bench card opens the inspect view (Identity / Runtime / Capabilities)
 * with an Edit affordance that flips to the editor in place. A save returns to inspect (now refreshed); a delete
 * closes the drawer. The async edit-load is resolved here so the inner views mount with their data ready.
 */
export function AgentDrawer({ mode, agentId, onClose }: { mode: "create" | "edit"; agentId?: string; onClose: () => void }) {
  const existing = useAgentDefinition(mode === "edit" ? agentId : undefined);
  const [editing, setEditing] = useState(mode === "create");

  if (mode === "create") {
    return <AgentEditorForm mode="create" initial={EMPTY_FORM} onCancel={onClose} onSaved={onClose} onDeleted={onClose} />;
  }

  if (existing.isLoading) {
    return <MessageDrawer onClose={onClose}><div className="wf-form-empty">Loading…</div></MessageDrawer>;
  }

  if (existing.error || !existing.data) {
    return (
      <MessageDrawer onClose={onClose}>
        <div className="cn-banner cn-banner-err">
          <div className="cn-banner-h">Couldn't load this agent</div>
          <div className="cn-banner-p">{existing.error instanceof ApiError ? existing.error.message : "The agent may not exist in this team."}</div>
        </div>
      </MessageDrawer>
    );
  }

  const persona = existing.data;

  if (editing) {
    return (
      <AgentEditorForm
        mode="edit"
        agentId={agentId}
        initial={formFromPersona(persona)}
        boundSkills={persona.boundSkills}
        immutableSlug={persona.slug}
        onCancel={() => setEditing(false)}
        onSaved={() => setEditing(false)}
        onDeleted={onClose}
      />
    );
  }

  return <AgentInspect agent={persona} onEdit={() => setEditing(true)} onClose={onClose} />;
}

/** Minimal drawer for the loading / error states — a plain head + the message. */
function MessageDrawer({ onClose, children }: { onClose: () => void; children: React.ReactNode }) {
  const head = (
    <div className="mdl-head">
      <div className="mdl-title-wrap"><div className="mdl-title">Agent</div></div>
      <DrawerClose onClose={onClose} />
    </div>
  );

  return <DrawerFrame label="Agent" onClose={onClose} head={head}>{children}</DrawerFrame>;
}
