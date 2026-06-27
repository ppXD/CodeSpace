import { useEffect, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentBoundSkill, AgentDefinitionInput, AgentDefinitionSummary } from "@/api/agents";
import { ApiError } from "@/api/request";
import { Combo, type Option } from "@/components/common/Combo";
import { useConfirm } from "@/components/dialog";
import { useAgentDefinition, useCreateAgent, useDeleteAgent, useUpdateAgent } from "@/hooks/use-agents";
import { useCredentialedModels } from "@/hooks/use-model-credentials";

import { deriveSlug } from "./deriveSlug";

/** The autonomy tiers the run path recognizes (AgentAutonomyLevel, parsed case-insensitively). Stored by name. */
const AUTONOMY: Option[] = [
  { value: "Confined", label: "Confined", desc: "Analysis only — no writes, no network." },
  { value: "Standard", label: "Standard", desc: "Writes inside its workspace, no network. The safe default." },
  { value: "Trusted", label: "Trusted", desc: "Workspace write + network — for runs that fetch dependencies." },
  { value: "Unleashed", label: "Unleashed", desc: "Highest capability — admin / controlled runners only." },
];
const DEFAULT_AUTONOMY = "Standard";
const TOOLS_MODES: Option[] = [
  { value: "inherit", label: "Inherit the harness default" },
  { value: "custom", label: "Custom allow-list" },
];

function normalizeAutonomy(stored: string | null | undefined): string {
  const match = AUTONOMY.find((t) => t.value.toLowerCase() === (stored ?? "").toLowerCase());
  return match ? match.value : DEFAULT_AUTONOMY;
}

function parseTools(text: string): string[] {
  return text.split(",").map((t) => t.trim()).filter((t) => t.length > 0);
}

interface FormState {
  name: string;
  description: string;
  systemPrompt: string;
  model: string;
  autonomy: string;
  toolsMode: "inherit" | "custom";
  toolsText: string;
}

const EMPTY_FORM: FormState = { name: "", description: "", systemPrompt: "", model: "", autonomy: DEFAULT_AUTONOMY, toolsMode: "inherit", toolsText: "" };

function formFromPersona(a: AgentDefinitionSummary): FormState {
  return {
    name: a.name,
    description: a.description ?? "",
    systemPrompt: a.systemPrompt ?? "",
    model: a.model ?? "",
    autonomy: normalizeAutonomy(a.defaultAutonomy),
    toolsMode: a.tools === null ? "inherit" : "custom",
    toolsText: a.tools ? a.tools.join(", ") : "",
  };
}

/**
 * Create / edit an Agent persona — a warm-theme MODAL over the authorable surface (name → derived @handle,
 * description, system prompt, model, default autonomy, tools). All dropdowns are the in-house warm {@link Combo}
 * (no native selects); the model picker loads the team's REAL credentialed models. A persona is
 * harness-AGNOSTIC (no harness field); skills are bound via import (shown read-only) — in-app binding is a follow-up.
 *
 * <p>The async edit-load is lifted here so the form mounts (and inits its state from props) only once data is
 * ready — no populate-from-query effect.</p>
 */
export function AgentEditorModal({ mode, agentId, onClose }: { mode: "create" | "edit"; agentId?: string; onClose: () => void }) {
  const existing = useAgentDefinition(mode === "edit" ? agentId : undefined);

  if (mode === "edit" && existing.isLoading) {
    return <ModalFrame title="Edit agent" onClose={onClose}><div className="ct-empty"><div className="ct-empty-h">Loading…</div></div></ModalFrame>;
  }
  if (mode === "edit" && (existing.error || !existing.data)) {
    return (
      <ModalFrame title="Edit agent" onClose={onClose}>
        <div className="cn-banner cn-banner-err">
          <div className="cn-banner-h">Couldn't load this agent</div>
          <div className="cn-banner-p">{existing.error instanceof ApiError ? existing.error.message : "The agent may not exist in this team."}</div>
        </div>
      </ModalFrame>
    );
  }

  return (
    <AgentEditorForm
      mode={mode}
      agentId={agentId}
      initial={mode === "edit" ? formFromPersona(existing.data!) : EMPTY_FORM}
      boundSkills={existing.data?.boundSkills}
      immutableSlug={existing.data?.slug}
      onClose={onClose}
    />
  );
}

/** The warm `.mdl` portal shell (mask + dialog + head + body + optional foot), reused for loading / error / form. */
function ModalFrame({ title, sub, onClose, foot, children }: { title: string; sub?: string; onClose: () => void; foot?: React.ReactNode; children: React.ReactNode }) {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" style={{ width: 560, maxWidth: "94vw" }}>
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">{title}</div>
            {sub && <div className="mdl-sub">{sub}</div>}
          </div>
          <button type="button" className="mdl-x" onClick={onClose} title="Close" aria-label="Close"><Ic.X size={14} /></button>
        </div>
        <div className="mdl-body">{children}</div>
        {foot}
      </div>
    </>,
    document.body,
  );
}

function AgentEditorForm({ mode, agentId, initial, boundSkills, immutableSlug, onClose }: { mode: "create" | "edit"; agentId?: string; initial: FormState; boundSkills?: AgentBoundSkill[]; immutableSlug?: string; onClose: () => void }) {
  const confirm = useConfirm();
  const credModels = useCredentialedModels();
  const createAgent = useCreateAgent();
  const updateAgent = useUpdateAgent();
  const deleteAgent = useDeleteAgent();

  const [name, setName] = useState(initial.name);
  const [description, setDescription] = useState(initial.description);
  const [systemPrompt, setSystemPrompt] = useState(initial.systemPrompt);
  const [model, setModel] = useState(initial.model);
  const [autonomy, setAutonomy] = useState(initial.autonomy);
  const [toolsMode, setToolsMode] = useState<"inherit" | "custom">(initial.toolsMode);
  const [toolsText, setToolsText] = useState(initial.toolsText);
  const [error, setError] = useState<string | null>(null);

  const handle = deriveSlug(name);
  const saving = createAgent.isPending || updateAgent.isPending;
  const canSave = name.trim().length > 0 && handle.length > 0 && !saving;

  // Real credentialed models, deduped by model id (the persona stores a model id, not a credential).
  const modelOpts: Option[] = [{ value: "", label: "Auto (harness default)" }];
  const seen = new Set<string>();
  for (const o of credModels.data ?? []) {
    if (seen.has(o.modelId)) continue;
    seen.add(o.modelId);
    modelOpts.push({ value: o.modelId, label: o.modelId, desc: `${o.provider}${o.tier && o.tier !== "Unknown" ? ` · ${o.tier}` : ""}${o.available === false ? " · offline" : ""}` });
  }
  if (model && !seen.has(model)) modelOpts.push({ value: model, label: model, desc: "not in your model list" });

  async function handleSave() {
    setError(null);
    const input: AgentDefinitionInput = {
      name: name.trim(),
      description: description.trim() || null,
      systemPrompt,
      model: model.trim() || null,
      defaultAutonomy: autonomy,
      tools: toolsMode === "inherit" ? null : parseTools(toolsText),
    };

    try {
      if (mode === "create") await createAgent.mutateAsync(input);
      else await updateAgent.mutateAsync({ id: agentId!, input });
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't save the agent.");
    }
  }

  async function handleDelete() {
    if (!agentId) return;
    const ok = await confirm({
      title: "Delete agent?",
      message: (<><strong>{name}</strong> will be removed. Its run history is kept; workflows referencing <code>@{immutableSlug}</code> will no longer resolve it.</>),
      confirmLabel: "Delete",
      destructive: true,
    });
    if (!ok) return;

    try {
      await deleteAgent.mutateAsync(agentId);
      onClose();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't delete the agent.");
    }
  }

  const foot = (
    <div className="mdl-foot">
      <div>
        {mode === "edit" && (
          <button type="button" className="btn btn-danger" onClick={handleDelete} disabled={deleteAgent.isPending}><Ic.Trash size={14} /> Delete</button>
        )}
      </div>
      <div style={{ display: "flex", gap: 8 }}>
        <button type="button" className="btn" onClick={onClose}>Cancel</button>
        <button type="button" className="btn btn-primary" onClick={handleSave} disabled={!canSave}>{saving ? "Saving…" : "Save"}</button>
      </div>
    </div>
  );

  return (
    <ModalFrame title={mode === "create" ? "New agent" : "Edit agent"} sub={mode === "create" ? "A reusable persona you can @-mention from a workflow." : `@${immutableSlug}`} onClose={onClose} foot={foot}>
      {error && (
        <div className="cn-banner cn-banner-err" style={{ marginBottom: 14 }}>
          <div className="cn-banner-h">Couldn't save</div>
          <div className="cn-banner-p">{error}</div>
        </div>
      )}

      <div className="wf-form">
        <div className="wf-form-row">
          <label className="wf-form-label" htmlFor="ag-name">Name<span className="wf-form-required">*</span></label>
          <input id="ag-name" className="wf-form-input" value={name} onChange={(e) => setName(e.target.value)} placeholder="Backend Architect" autoFocus={mode === "create"} />
          <span className="wf-form-help">
            {mode === "edit"
              ? <>Handle <code>@{immutableSlug}</code> is fixed (the @-mention other workflows reference).</>
              : handle.length > 0
                ? <>Handle will be <code>@{handle}</code> — derived from the name, fixed after creation.</>
                : "Use a name with at least one letter or digit so a handle can be derived."}
          </span>
        </div>

        <div className="wf-form-row">
          <label className="wf-form-label" htmlFor="ag-desc">Description</label>
          <input id="ag-desc" className="wf-form-input" value={description} onChange={(e) => setDescription(e.target.value)} placeholder="Use PROACTIVELY for system design and API boundaries." />
        </div>

        <div className="wf-form-row">
          <label className="wf-form-label" htmlFor="ag-prompt">System prompt</label>
          <textarea id="ag-prompt" className="wf-form-textarea" value={systemPrompt} onChange={(e) => setSystemPrompt(e.target.value)} rows={7} placeholder="You are a senior backend architect. Before writing code, lay out the data model and the API surface…" />
        </div>

        <div className="wf-form-row">
          <span className="wf-form-label">Model</span>
          <Combo value={model} options={modelOpts} onChange={setModel} placeholder="Auto (harness default)" searchable />
          <span className="wf-form-help">Auto = let the chosen harness pick its default. The list is your team's credentialed models.</span>
        </div>

        <div className="wf-form-row">
          <span className="wf-form-label">Default autonomy</span>
          <Combo value={autonomy} options={AUTONOMY} onChange={setAutonomy} />
          <span className="wf-form-help">{AUTONOMY.find((t) => t.value === autonomy)?.desc}</span>
        </div>

        <div className="wf-form-row">
          <span className="wf-form-label">Tools</span>
          <Combo value={toolsMode} options={TOOLS_MODES} onChange={(v) => setToolsMode(v as "inherit" | "custom")} />
          {toolsMode === "custom" && (
            <>
              <input className="wf-form-input" style={{ marginTop: 6 }} value={toolsText} onChange={(e) => setToolsText(e.target.value)} placeholder="read, edit, run_command" />
              <span className="wf-form-help">Comma-separated. Empty = no tools.</span>
            </>
          )}
        </div>

        {mode === "edit" && (
          <div className="wf-form-row">
            <span className="wf-form-label">Bound skills</span>
            {boundSkills && boundSkills.length > 0
              ? <div className="wf-triggers">{boundSkills.map((s) => <span key={s.skillDefinitionId} className="wf-trigger-chip" title={s.name}>{s.slug}</span>)}</div>
              : <span className="wf-trigger-muted">none</span>}
            <span className="wf-form-help">Skills are bound when importing a pack; in-app binding is coming soon.</span>
          </div>
        )}
      </div>
    </ModalFrame>
  );
}
