import { useState } from "react";
import { useNavigate } from "@tanstack/react-router";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { AgentBoundSkill, AgentDefinitionInput, AgentDefinitionSummary } from "@/api/agents";
import { ApiError } from "@/api/request";
import { useConfirm } from "@/components/dialog";
import { useAgentDefinition, useCreateAgent, useDeleteAgent, useHarnesses, useUpdateAgent } from "@/hooks/use-agents";

import { deriveSlug } from "./deriveSlug";

/** The autonomy tiers the run path recognizes (AgentAutonomyLevel, parsed case-insensitively). Stored by name. */
const AUTONOMY: { value: string; desc: string }[] = [
  { value: "Confined", desc: "Analysis only — no writes, no network. The most restricted tier." },
  { value: "Standard", desc: "Writes inside its workspace, no network. The safe default." },
  { value: "Trusted", desc: "Workspace write + network — for runs that fetch dependencies." },
  { value: "Unleashed", desc: "Highest capability — admin / controlled runners only." },
];
const DEFAULT_AUTONOMY = "Standard";

/** Map a stored autonomy string (any case, possibly a legacy value) to a canonical tier, falling back to the safe default. */
function normalizeAutonomy(stored: string | null | undefined): string {
  const match = AUTONOMY.find((t) => t.value.toLowerCase() === (stored ?? "").toLowerCase());
  return match ? match.value : DEFAULT_AUTONOMY;
}

/** Comma-separated tools → a trimmed, non-empty list (empty input ⇒ [], meaning "no tools"). */
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
 * Create / edit an Agent persona — the authorable surface (name → derived @handle, description, system prompt,
 * model, default autonomy, tools). A persona is harness-AGNOSTIC, so there's no harness field; skills are bound
 * via import (shown read-only here) — in-app binding is a follow-up.
 *
 * <p>The async edit-load is lifted here so the form mounts ONLY once data is ready — its state initialises from
 * props, avoiding a populate-from-query effect (no cascading-render setState).</p>
 */
export function AgentEditor({ mode, teamSlug, agentId }: { mode: "create" | "edit"; teamSlug: string; agentId?: string }) {
  const existing = useAgentDefinition(mode === "edit" ? agentId : undefined);

  if (mode === "edit") {
    if (existing.isLoading) {
      return <section className="ct"><div className="ct-body"><div className="ct-empty"><div className="ct-empty-h">Loading…</div></div></div></section>;
    }
    if (existing.error || !existing.data) {
      return (
        <section className="ct"><div className="ct-body">
          <div className="cn-banner cn-banner-err" style={{ margin: 16 }}>
            <div className="cn-banner-h">Couldn't load this agent</div>
            <div className="cn-banner-p">{existing.error instanceof ApiError ? existing.error.message : "The agent may not exist in this team."}</div>
          </div>
        </div></section>
      );
    }
    return <AgentEditorForm mode="edit" teamSlug={teamSlug} agentId={agentId} initial={formFromPersona(existing.data)} boundSkills={existing.data.boundSkills} immutableSlug={existing.data.slug} />;
  }

  return <AgentEditorForm mode="create" teamSlug={teamSlug} initial={EMPTY_FORM} />;
}

function AgentEditorForm({ mode, teamSlug, agentId, initial, boundSkills, immutableSlug }: { mode: "create" | "edit"; teamSlug: string; agentId?: string; initial: FormState; boundSkills?: AgentBoundSkill[]; immutableSlug?: string }) {
  const navigate = useNavigate();
  const confirm = useConfirm();
  const harnesses = useHarnesses();
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

  const modelSuggestions = [...new Set((harnesses.data ?? []).flatMap((h) => h.models))].sort();

  const toList = () => navigate({ to: "/teams/$teamSlug/agents", params: { teamSlug } });

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
      toList();
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
      toList();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Couldn't delete the agent.");
    }
  }

  return (
    <section className="ct">
      <div className="ct-head" style={{ paddingBottom: 18 }}>
        <div className="ct-crumbs">
          <span className="cur-link" onClick={toList} style={{ cursor: "pointer" }}>Agents</span>
          <span> › {mode === "create" ? "New agent" : name || "Edit"}</span>
        </div>
        <div className="ct-title-row">
          <h1 className="ct-title">{mode === "create" ? "New agent" : "Edit agent"}</h1>
          <div className="ct-actions">
            {mode === "edit" && (
              <button type="button" className="btn btn-danger" onClick={handleDelete} disabled={deleteAgent.isPending}>
                <Ic.Trash size={14} /> Delete
              </button>
            )}
            <button type="button" className="btn" onClick={toList}><Ic.X size={14} /> Cancel</button>
            <button type="button" className="btn btn-primary" onClick={handleSave} disabled={!canSave}>
              <Ic.Check size={14} /> {saving ? "Saving…" : "Save"}
            </button>
          </div>
        </div>
      </div>

      <div className="ct-body">
        {error && (
          <div className="cn-banner cn-banner-err" style={{ marginBottom: 16 }}>
            <div className="cn-banner-h">Couldn't save</div>
            <div className="cn-banner-p">{error}</div>
          </div>
        )}

        <div style={{ display: "flex", gap: 24, alignItems: "flex-start", flexWrap: "wrap", paddingTop: 4 }}>
          {/* Main: the authored content */}
          <div className="wf-form" style={{ flex: "1 1 360px", minWidth: 0 }}>
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
              <span className="wf-form-help">Drives auto-invocation + the library row. Optional.</span>
            </div>

            <div className="wf-form-row">
              <label className="wf-form-label" htmlFor="ag-prompt">System prompt</label>
              <textarea id="ag-prompt" className="wf-form-textarea" value={systemPrompt} onChange={(e) => setSystemPrompt(e.target.value)} rows={12} placeholder="You are a senior backend architect. Before writing code, lay out the data model and the API surface…" />
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

          {/* Rail: the composition */}
          <div className="wf-form" style={{ flex: "0 1 280px" }}>
            <div className="wf-form-row">
              <label className="wf-form-label" htmlFor="ag-model">Model</label>
              <input id="ag-model" className="wf-form-input" value={model} onChange={(e) => setModel(e.target.value)} placeholder="default" list="ag-model-suggestions" />
              <datalist id="ag-model-suggestions">{modelSuggestions.map((m) => <option key={m} value={m} />)}</datalist>
              <span className="wf-form-help">Blank = let the chosen harness pick its default.</span>
            </div>

            <div className="wf-form-row">
              <label className="wf-form-label" htmlFor="ag-autonomy">Default autonomy</label>
              <select id="ag-autonomy" className="wf-form-input" value={autonomy} onChange={(e) => setAutonomy(e.target.value)}>
                {AUTONOMY.map((t) => <option key={t.value} value={t.value}>{t.value}</option>)}
              </select>
              <span className="wf-form-help">{AUTONOMY.find((t) => t.value === autonomy)?.desc}</span>
            </div>

            <div className="wf-form-row">
              <span className="wf-form-label">Tools</span>
              <label className="wf-form-check"><input type="radio" name="ag-tools" checked={toolsMode === "inherit"} onChange={() => setToolsMode("inherit")} /> Inherit the harness default</label>
              <label className="wf-form-check"><input type="radio" name="ag-tools" checked={toolsMode === "custom"} onChange={() => setToolsMode("custom")} /> Custom allow-list</label>
              {toolsMode === "custom" && (
                <>
                  <input className="wf-form-input" value={toolsText} onChange={(e) => setToolsText(e.target.value)} placeholder="read, edit, run_command" />
                  <span className="wf-form-help">Comma-separated. Empty = no tools.</span>
                </>
              )}
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}
