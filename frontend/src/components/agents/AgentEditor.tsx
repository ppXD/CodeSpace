import { useEffect, useRef, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentBoundSkill, AgentDefinitionInput } from "@/api/agents";
import { ApiError } from "@/api/request";
import { Combo, type Option } from "@/components/common/Combo";
import { useConfirm } from "@/components/dialog";
import { useAgentDefinition, useCreateAgent, useDeleteAgent, useSetAgentSkills, useUpdateAgent } from "@/hooks/use-agents";
import { useCredentialedModels } from "@/hooks/use-model-credentials";
import { useSkills } from "@/hooks/use-skills";

import { AUTONOMY, EMPTY_FORM, type FormState, formFromPersona, parseTools, TOOLS_MODES } from "./agentForm";
import { deriveSlug } from "./deriveSlug";
import { SkillLibraryPickerModal } from "./SkillLibraryPickerModal";
import { skillLabels } from "./skillPicker";

/**
 * Create / edit an Agent persona — a warm-theme centered MODAL over the authorable surface, grouped into three
 * sections (Identity / Runtime / Capabilities). All dropdowns are the in-house warm {@link Combo}; the model
 * picker loads the team's REAL credentialed models. A persona is harness-AGNOSTIC (no harness field); skills are
 * bound in the Capabilities section (or via a pack import) and persisted on Save. The async edit-load is lifted
 * here so the form mounts (and inits its state from props) only once data is ready — no populate-from-query effect.
 */
export function AgentEditorModal({ mode, agentId, onClose }: { mode: "create" | "edit"; agentId?: string; onClose: () => void }) {
  const existing = useAgentDefinition(mode === "edit" ? agentId : undefined);

  if (mode === "edit" && existing.isLoading) {
    return <ModalFrame title="Edit agent" onClose={onClose}><div className="wf-form-empty">Loading…</div></ModalFrame>;
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
function ModalFrame({ title, sub, onClose, foot, escapeDisabled, children }: { title: string; sub?: React.ReactNode; onClose: () => void; foot?: React.ReactNode; escapeDisabled?: boolean; children: React.ReactNode }) {
  useEffect(() => {
    // Suspend Escape while a layered dialog (the delete-confirm) is open, so one Escape cancels only that
    // dialog rather than also tearing down the editor underneath it.
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape" && !escapeDisabled) onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose, escapeDisabled]);

  return createPortal(
    <>
      <div className="mdl-mask" onClick={onClose} />
      <div className="mdl" role="dialog" aria-modal="true" aria-label={title} style={{ width: 680, maxWidth: "94vw" }}>
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
  const setSkills = useSetAgentSkills();

  const [name, setName] = useState(initial.name);
  const [description, setDescription] = useState(initial.description);
  const [systemPrompt, setSystemPrompt] = useState(initial.systemPrompt);
  const [model, setModel] = useState(initial.model);
  const [autonomy, setAutonomy] = useState(initial.autonomy);
  const [toolsMode, setToolsMode] = useState<"inherit" | "custom">(initial.toolsMode);
  const [toolsText, setToolsText] = useState(initial.toolsText);
  const [skillIds, setSkillIds] = useState<string[]>(() => (boundSkills ?? []).map((s) => s.skillDefinitionId));
  const [error, setError] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  // Survives a partial-create retry: once create succeeds, a re-Save reuses this id (update + re-bind) rather
  // than re-creating and colliding on the unique handle.
  const createdIdRef = useRef<string | null>(null);

  const handle = deriveSlug(name);
  const saving = createAgent.isPending || updateAgent.isPending || setSkills.isPending;
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
      // Skills are a separate join (not part of AgentDefinitionInput): persist the fields first, then replace
      // the bound set. On create the agent must exist before it can be bound, so we use the returned id. If a
      // prior attempt already created the agent but the binding failed, createdIdRef holds that id — reuse it
      // (update + re-bind) instead of re-creating, which would collide on the unique handle.
      if (mode === "edit") {
        await updateAgent.mutateAsync({ id: agentId!, input });
        await setSkills.mutateAsync({ id: agentId!, skillIds });
      } else if (createdIdRef.current === null) {
        const created = await createAgent.mutateAsync(input);
        createdIdRef.current = created.id;
        if (skillIds.length > 0) await setSkills.mutateAsync({ id: created.id, skillIds });
      } else {
        await updateAgent.mutateAsync({ id: createdIdRef.current, input });
        await setSkills.mutateAsync({ id: createdIdRef.current, skillIds });
      }
      onClose();
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
    <ModalFrame title={mode === "create" ? "New agent" : "Edit agent"} sub={mode === "create" ? "A reusable unit you can @-mention from a workflow." : <>@{immutableSlug}</>} onClose={onClose} escapeDisabled={confirming} foot={foot}>
      {error && (
        <div className="cn-banner cn-banner-err" style={{ marginBottom: 14 }}>
          <div className="cn-banner-h">Couldn't save</div>
          <div className="cn-banner-p">{error}</div>
        </div>
      )}

      <div className="wf-form">
        <div className="ed-sec-h"><Ic.Bot size={13} /> Identity</div>

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

        <div className="ed-sec-h" style={{ marginTop: 6 }}><Ic.Sparkles size={13} /> Runtime</div>

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

        <div className="ed-sec-h" style={{ marginTop: 6 }}><Ic.Puzzle size={13} /> Capabilities</div>
        <div className="wf-form-row">
          <span className="wf-form-label">Bound skills</span>
          <SkillPicker selected={skillIds} onChange={setSkillIds} boundSkills={boundSkills} />
          <span className="wf-form-help">Skills the agent loads when it runs. Add your team's skills, or import a pack to bring in more.</span>
        </div>
      </div>
    </ModalFrame>
  );
}

/**
 * Editable skill-binding control. Bound skills show as removable chips; "Add skill" opens the Library picker, which
 * instantiates a working copy of the chosen store skill and hands back its id (the "instantiate to use" model). The
 * parent persists the bound set on Save. Labels resolve from the team's working skills once the new copy lands.
 */
function SkillPicker({ selected, onChange, boundSkills }: { selected: string[]; onChange: (ids: string[]) => void; boundSkills?: AgentBoundSkill[] }) {
  const skills = useSkills();
  const [picking, setPicking] = useState(false);

  const labels = skillLabels(skills.data ?? [], boundSkills ?? []);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 8 }}>
      {selected.length > 0 && (
        <div className="wf-triggers">
          {selected.map((id) => (
            <span key={id} className="wf-trigger-chip">
              {labels.get(id) ?? "skill"}
              <button type="button" className="ed-tok-x" aria-label={`Remove ${labels.get(id) ?? "skill"}`} onClick={() => onChange(selected.filter((x) => x !== id))}><Ic.X size={11} /></button>
            </span>
          ))}
        </div>
      )}

      <button type="button" className="btn" style={{ alignSelf: "flex-start" }} onClick={() => setPicking(true)}><Ic.Plus size={13} /> Add skill</button>

      {picking && (
        <SkillLibraryPickerModal
          onPicked={(id) => { if (!selected.includes(id)) onChange([...selected, id]); setPicking(false); }}
          onClose={() => setPicking(false)}
        />
      )}
    </div>
  );
}
