import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { VariableSummary, VariableValueType } from "@/api/variables";
import {
  useDeleteProjectVariable,
  useDeleteTeamVariable,
  useDeleteWorkflowVariable,
  useProjectVariables,
  useSetProjectVariable,
  useSetTeamVariable,
  useSetWorkflowVariable,
  useTeamVariables,
  useWorkflowVariables,
} from "@/hooks/use-variables";

/**
 * Generic variable panel — single component used by the workflow editor's Variables tab
 * (wf.* scope), the team-settings Variables page (team.* scope), AND the project-detail
 * Variables tab (project.{slug}.* scope).
 *
 * Variables live in their own DB table; every Add/Edit/Delete persists immediately via the
 * API. No editor-side Save step.
 *
 * Secret values get a `<SecretValueEditor>` (password-masked input + Save / Replace / Clear).
 * Plain values get a type-appropriate inline editor. Adding a row creates an empty String
 * variable that the operator then fills.
 */

interface VariableTablePanelProps {
  scope: "Team" | "Workflow" | "Project";
  /** Required when scope === "Workflow"; unused otherwise. */
  workflowId?: string;
  /** Required when scope === "Project"; unused otherwise. */
  projectId?: string;
  /**
   * Variable-path head shown on the ref-copy chip. For Team it's <c>"team"</c>; for
   * Workflow it's <c>"wf"</c>; for Project it's <c>"project.{slug}"</c> (so the chip
   * copies <c>{{project.{slug}.{name}}}</c> verbatim — operators paste straight into a
   * node config). Caller passes the prefix because the slug isn't known to this component.
   */
  refPrefix: string;
  title: string;
  subtitle: string;
  tip: string;
  emptyHint: string;
}

const SCHEMA_TYPES: VariableValueType[] = ["String", "Number", "Boolean", "Object", "Array", "Secret"];

export function VariableTablePanel({ scope, workflowId, projectId, refPrefix, title, subtitle, tip, emptyHint }: VariableTablePanelProps) {
  const teamList = useTeamVariables();
  const wfList = useWorkflowVariables(scope === "Workflow" ? (workflowId ?? null) : null);
  const projList = useProjectVariables(scope === "Project" ? (projectId ?? null) : null);
  const list = scope === "Team" ? teamList : scope === "Workflow" ? wfList : projList;

  const setTeam = useSetTeamVariable();
  const setWf = useSetWorkflowVariable(workflowId ?? null);
  const setProj = useSetProjectVariable(projectId ?? null);
  const delTeam = useDeleteTeamVariable();
  const delWf = useDeleteWorkflowVariable(workflowId ?? null);
  const delProj = useDeleteProjectVariable(projectId ?? null);

  const isMutating =
    setTeam.isPending || setWf.isPending || setProj.isPending ||
    delTeam.isPending || delWf.isPending || delProj.isPending;

  const setVar = (name: string, valueType: VariableValueType, value: unknown, description: string | null) => {
    const input = { valueType, value, description };
    if (scope === "Team") return setTeam.mutateAsync({ name, input });
    if (scope === "Workflow") return setWf.mutateAsync({ name, input });
    return setProj.mutateAsync({ name, input });
  };

  const deleteVar = (name: string) => {
    if (scope === "Team") return delTeam.mutateAsync(name);
    if (scope === "Workflow") return delWf.mutateAsync(name);
    return delProj.mutateAsync(name);
  };

  const variables = list.data ?? [];

  const addEmpty = async () => {
    const name = nextName(variables);
    await setVar(name, "String", "", null);
  };

  return (
    <div className="wf-vars-panel">
      <header className="wf-vars-panel-head">
        <div className="wf-vars-panel-titlebox">
          <h3 className="wf-vars-panel-title">{title}</h3>
          <p className="wf-vars-panel-subtitle">{subtitle}</p>
        </div>
        <button type="button" className="btn btn-ghost wf-vars-panel-add" onClick={addEmpty} disabled={isMutating}>
          <Ic.Plus size={12} /> Add variable
        </button>
      </header>

      <div className="wf-vars-panel-tip" role="note">
        <strong>TIPS</strong>
        <span>{tip}</span>
      </div>

      {list.isLoading && <div className="wf-vars-panel-empty">Loading…</div>}

      {list.isError && (
        <div className="wf-vars-panel-empty" role="alert">
          Failed to load variables. Refresh the page to retry.
        </div>
      )}

      {!list.isLoading && !list.isError && variables.length === 0 && (
        <div className="wf-vars-panel-empty">{emptyHint}</div>
      )}

      {variables.length > 0 && (
        <ul className="wf-vars-list">
          {variables.map((v) => (
            <VariableRow
              key={v.id}
              refPrefix={refPrefix}
              variable={v}
              isMutating={isMutating}
              onSetType={(newType) => setVar(v.name, newType, defaultFor(newType), v.description)}
              onSetDescription={(desc) => setVar(v.name, v.valueType, parsePlain(v.valuePlain, v.valueType), desc)}
              onSetValue={(val) => setVar(v.name, v.valueType, val, v.description)}
              onRename={async (next) => {
                // Rename = delete-old + create-new. Order matters: create first to fail-fast
                // on name conflict, then delete the old row.
                await setVar(next, v.valueType, parsePlain(v.valuePlain, v.valueType), v.description);
                await deleteVar(v.name);
              }}
              onRemove={() => deleteVar(v.name)}
            />
          ))}
        </ul>
      )}
    </div>
  );
}

// ─── Single row ────────────────────────────────────────────────────────────────

interface VariableRowProps {
  refPrefix: string;
  variable: VariableSummary;
  isMutating: boolean;
  onSetType: (next: VariableValueType) => Promise<void>;
  onSetDescription: (next: string | null) => Promise<void>;
  onSetValue: (next: unknown) => Promise<void>;
  onRename: (nextName: string) => Promise<void>;
  onRemove: () => Promise<void>;
}

function VariableRow({ refPrefix, variable, isMutating, onSetType, onSetDescription, onSetValue, onRename, onRemove }: VariableRowProps) {
  const [expanded, setExpanded] = useState(false);
  const [nameDraft, setNameDraft] = useState(variable.name);
  const [descDraft, setDescDraft] = useState(variable.description ?? "");

  const hasValueSet = variable.valueType === "Secret"
    ? true   // Secret rows are always "set" once they exist — value is encrypted in DB
    : (variable.valuePlain != null && variable.valuePlain !== "" && variable.valuePlain !== "\"\"");

  return (
    <li className="wf-vars-row" data-expanded={expanded}>
      <div className="wf-vars-row-h">
        <input
          className="wf-form-input wf-vars-row-name"
          value={nameDraft}
          onChange={(e) => setNameDraft(e.target.value)}
          onBlur={() => { if (nameDraft && nameDraft !== variable.name) void onRename(nameDraft); }}
          placeholder="name"
        />
        <select
          className="wf-form-input wf-vars-row-type"
          value={variable.valueType}
          onChange={(e) => void onSetType(e.target.value as VariableValueType)}
          disabled={isMutating}
        >
          {SCHEMA_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        <span className="wf-vars-row-envstate" data-set={hasValueSet ? "true" : "false"}>
          <span className="wf-vars-row-envstate-dot" aria-hidden />
          {hasValueSet ? (variable.valueType === "Secret" ? "value set" : "set") : "no value"}
        </span>
        <button type="button" className="btn btn-ghost wf-vars-row-toggle" onClick={() => setExpanded((v) => !v)} title={expanded ? "Hide details" : "Show details"}>
          {expanded ? <Ic.ChevronDown size={11} /> : <Ic.ChevronRight size={11} />}
        </button>
        <button type="button" className="btn btn-ghost wf-vars-row-remove" onClick={() => void onRemove()} title="Remove" disabled={isMutating}>
          <Ic.Trash size={11} />
        </button>
      </div>

      <code className="wf-vars-row-ref" title="Click to copy" onClick={() => navigator.clipboard?.writeText(`{{${refPrefix}.${variable.name}}}`)}>
        {`{{${refPrefix}.${variable.name}}}`}
      </code>

      {expanded && (
        <div className="wf-vars-row-detail">
          <label className="wf-form-row">
            <span className="wf-form-label">Description</span>
            <input
              className="wf-form-input"
              value={descDraft}
              onChange={(e) => setDescDraft(e.target.value)}
              onBlur={() => { if (descDraft !== (variable.description ?? "")) void onSetDescription(descDraft || null); }}
              placeholder="One-line hint shown to operators"
            />
          </label>

          {variable.valueType === "Secret"
            ? <SecretValueEditor onSave={(plain) => onSetValue(plain)} isMutating={isMutating} />
            : <PlainValueEditor valueType={variable.valueType} valuePlain={variable.valuePlain} isMutating={isMutating}
                                onSave={(val) => onSetValue(val)} />}
        </div>
      )}
    </li>
  );
}

// ─── Secret value editor (password-masked, Save / Replace / Clear) ────────────

interface SecretValueEditorProps {
  isMutating: boolean;
  onSave: (plain: string) => Promise<void>;
}

function SecretValueEditor({ isMutating, onSave }: SecretValueEditorProps) {
  const [draft, setDraft] = useState("");
  const [show, setShow] = useState(false);

  const save = async () => {
    if (draft.length === 0) return;
    await onSave(draft);
    setDraft("");
  };

  return (
    <div className="wf-form-row wf-vars-row-envvalue">
      <span className="wf-form-label">Value</span>
      <div className="wf-vars-row-envvalue-edit">
        <input
          className="wf-form-input wf-vars-row-envvalue-input"
          type={show ? "text" : "password"}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder="Enter NEW value (existing value hidden — secrets never returned)"
          autoComplete="off"
          spellCheck={false}
        />
        <button type="button" className="btn btn-ghost" onClick={() => setShow((s) => !s)} title={show ? "Hide" : "Show"}>
          {show ? <Ic.EyeOff size={11} /> : <Ic.Eye size={11} />}
        </button>
        <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={isMutating || draft.length === 0}>
          Save
        </button>
      </div>
      <span className="wf-form-help">Stored encrypted (AES-256-GCM). Plaintext only crosses the wire on Save; never returned by the API.</span>
    </div>
  );
}

// ─── Plain value editor (type-aware input) ───────────────────────────────────

interface PlainValueEditorProps {
  valueType: VariableValueType;
  valuePlain: string | null;
  isMutating: boolean;
  onSave: (value: unknown) => Promise<void>;
}

function PlainValueEditor({ valueType, valuePlain, isMutating, onSave }: PlainValueEditorProps) {
  const [draft, setDraft] = useState(valuePlain ?? "");

  const save = async () => {
    const parsed = parsePlainAs(valueType, draft);
    await onSave(parsed);
  };

  return (
    <label className="wf-form-row">
      <span className="wf-form-label">Value</span>
      <div className="wf-vars-row-envvalue-edit">
        <input
          className="wf-form-input wf-vars-row-envvalue-input"
          type={valueType === "Number" ? "number" : "text"}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder={valueType === "Object" || valueType === "Array" ? "Enter raw JSON" : ""}
        />
        <button type="button" className="btn btn-primary" onClick={() => void save()} disabled={isMutating}>
          Save
        </button>
      </div>
      <span className="wf-form-help">
        {valueType === "Object" || valueType === "Array" ? "Stored as JSON. Validate your input — malformed JSON is rejected." : "Stored as JSON-encoded text."}
      </span>
    </label>
  );
}

// ─── Helpers ───────────────────────────────────────────────────────────────────

function nextName(existing: { name: string }[]): string {
  const taken = new Set(existing.map((v) => v.name));
  let i = 1;
  while (taken.has(`var${i}`)) i++;
  return `var${i}`;
}

function defaultFor(valueType: VariableValueType): unknown {
  if (valueType === "Secret") return "";
  if (valueType === "String") return "";
  if (valueType === "Number") return 0;
  if (valueType === "Boolean") return false;
  if (valueType === "Object") return {};
  if (valueType === "Array") return [];
  return "";
}

function parsePlain(valuePlain: string | null, valueType: VariableValueType): unknown {
  if (valueType === "Secret") return "";   // never available; caller shouldn't ask
  if (valuePlain == null || valuePlain === "") return defaultFor(valueType);
  try { return JSON.parse(valuePlain); }
  catch { return valuePlain; }
}

function parsePlainAs(valueType: VariableValueType, draft: string): unknown {
  if (valueType === "String") return draft;
  if (valueType === "Number") {
    const n = Number(draft);
    return Number.isFinite(n) ? n : 0;
  }
  if (valueType === "Boolean") return draft === "true" || draft === "1";
  // Object / Array — best-effort JSON parse. If it fails, send the raw string and let the
  // backend reject; this matches the "validate your input" hint.
  try { return JSON.parse(draft); }
  catch { return draft; }
}
