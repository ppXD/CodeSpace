import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentBoundSkill, AgentDefinitionInput } from "@/api/agents";
import { ApiError } from "@/api/request";
import { Combo, type Option } from "@/components/common/Combo";
import { useConfirm } from "@/components/dialog";
import { useCreateAgent, useDeleteAgent, useUpdateAgent } from "@/hooks/use-agents";
import { useCredentialedModels } from "@/hooks/use-model-credentials";

import { AUTONOMY, type FormState, parseTools, TOOLS_MODES } from "./agentForm";
import { deriveSlug } from "./deriveSlug";
import { DrawerClose, DrawerFrame } from "./DrawerFrame";

/**
 * The agent editor — the edit mode of the detail drawer. Renders the authorable surface (name → derived @handle,
 * description, system prompt, model, default autonomy, tools) grouped into the same three sections as the inspect
 * view: Identity, Runtime, Capabilities. All dropdowns are the in-house warm {@link Combo}; the model picker loads
 * the team's REAL credentialed models. A persona is harness-AGNOSTIC (no harness field); skills are bound via
 * import (shown read-only) — in-app binding is a follow-up.
 *
 * Callbacks: onCancel (dismiss without saving — back to inspect, or close on create), onSaved (a create/update
 * landed), onDeleted (the persona was removed). The host (AgentDrawer) decides what each means.
 */
export function AgentEditorForm({ mode, agentId, initial, boundSkills, immutableSlug, onCancel, onSaved, onDeleted }: { mode: "create" | "edit"; agentId?: string; initial: FormState; boundSkills?: AgentBoundSkill[]; immutableSlug?: string; onCancel: () => void; onSaved: () => void; onDeleted: () => void }) {
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
  const [confirming, setConfirming] = useState(false);

  const handle = deriveSlug(name);
  const saving = createAgent.isPending || updateAgent.isPending;
  const canSave = name.trim().length > 0 && handle.length > 0 && !saving;

  // Every credentialed model the team exposes (NOT deduped — same as the launch composer; the credential
  // name in the description distinguishes two credentials offering the same model id). The persona stores
  // just the model id, so picking either row of a shared id persists that id.
  const modelDesc = (o: { provider: string; credentialName: string; tier?: string | null; available?: boolean | null }) =>
    `${o.provider} · ${o.credentialName}${o.tier && o.tier !== "Unknown" ? ` · ${o.tier}` : ""}${o.available === false ? " · offline" : ""}`;
  const modelOpts: Option[] = [
    { value: "", label: "Auto (harness default)" },
    ...(credModels.data ?? []).map((o) => ({ value: o.modelId, label: o.modelId, desc: modelDesc(o) })),
  ];
  if (model && !(credModels.data ?? []).some((o) => o.modelId === model)) modelOpts.push({ value: model, label: model, desc: "not in your model list" });

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
      onSaved();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't save the agent.");
    }
  }

  async function handleDelete() {
    if (!agentId) return;
    setConfirming(true);
    const ok = await confirm({
      title: "Delete agent?",
      message: (<><strong>{name}</strong> will be removed. Its run history is kept; workflows referencing <code>@{immutableSlug}</code> will no longer resolve it.</>),
      confirmLabel: "Delete",
      destructive: true,
    });
    setConfirming(false);
    if (!ok) return;

    try {
      await deleteAgent.mutateAsync(agentId);
      onDeleted();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't delete the agent.");
    }
  }

  const head = (
    <div className="mdl-head">
      <div className="mdl-title-wrap">
        <div className="mdl-title">{mode === "create" ? "New agent" : "Edit agent"}</div>
        <div className="mdl-sub">{mode === "create" ? "A reusable unit you can @-mention from a workflow." : <>@{immutableSlug}</>}</div>
      </div>
      <DrawerClose onClose={onCancel} />
    </div>
  );

  const foot = (
    <div className="mdl-foot">
      <div>
        {mode === "edit" && (
          <button type="button" className="btn btn-danger" onClick={handleDelete} disabled={deleteAgent.isPending}><Ic.Trash size={14} /> Delete</button>
        )}
      </div>
      <div style={{ display: "flex", gap: 8 }}>
        <button type="button" className="btn" onClick={onCancel}>{mode === "create" ? "Cancel" : "Back"}</button>
        <button type="button" className="btn btn-primary" onClick={handleSave} disabled={!canSave}>{saving ? "Saving…" : "Save"}</button>
      </div>
    </div>
  );

  return (
    <DrawerFrame label={mode === "create" ? "New agent" : "Edit agent"} onClose={onCancel} escapeDisabled={confirming} head={head} foot={foot}>
      {error && (
        <div className="cn-banner cn-banner-err" style={{ marginBottom: 14 }}>
          <div className="cn-banner-h">Couldn't save</div>
          <div className="cn-banner-p">{error}</div>
        </div>
      )}

      <div className="wf-form">
        <div className="drw-sec-h"><Ic.Bot size={13} /> Identity</div>

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

        <div className="drw-sec-h" style={{ marginTop: 6 }}><Ic.Sparkles size={13} /> Runtime</div>

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
          <>
            <div className="drw-sec-h" style={{ marginTop: 6 }}><Ic.Puzzle size={13} /> Capabilities</div>
            <div className="wf-form-row">
              <span className="wf-form-label">Bound skills</span>
              {boundSkills && boundSkills.length > 0
                ? <div className="wf-triggers">{boundSkills.map((s) => <span key={s.skillDefinitionId} className="wf-trigger-chip" title={s.name}>{s.slug}</span>)}</div>
                : <span className="wf-trigger-muted">none</span>}
              <span className="wf-form-help">Skills are bound when importing a pack; in-app binding is coming soon.</span>
            </div>
          </>
        )}
      </div>
    </DrawerFrame>
  );
}
