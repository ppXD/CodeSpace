import { useState } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { SystemVariableDto, WorkflowVariable } from "@/api/workflows";

/**
 * Drawer panel for the workflow's IO contract + the read-only System scope. Three kinds:
 *   - inputs (input.*)  — per-run parameters declared in the workflow definition
 *   - outputs (output.*) — what the workflow returns on Success
 *   - system (sys.*)    — engine-injected per-run context (read-only)
 *
 * <para>Inputs + Outputs ARE part of the workflow definition JSON (the IO contract callers
 * see), so this panel drives editor-Save-time persistence. The wf.* and team.* scopes live
 * in the unified `variable` table; their UI is <see cref="VariableTablePanel"/>.</para>
 */

export type VariableKind = "inputs" | "outputs" | "system";

interface ScopeMeta {
  title: string;
  subtitle: string;
  tip: string;
  refPrefix: string;
  editable: boolean;
  addLabel?: string;
  emptyHint?: string;
}

const KIND_LABELS: Record<VariableKind, ScopeMeta> = {
  inputs: {
    title: "Inputs",
    subtitle: "Per-run parameters from the caller. {{input.<name>}}",
    tip: "The Run-now form builds from this list.",
    refPrefix: "input",
    editable: true,
    addLabel: "Add input",
    emptyHint: "No inputs yet.",
  },
  outputs: {
    title: "Outputs",
    subtitle: "What this workflow returns on Success. {{output.<name>}}",
    tip: "Filled by the Terminal node's Inputs map.",
    refPrefix: "output",
    editable: true,
    addLabel: "Add output",
    emptyHint: "No outputs yet.",
  },
  system: {
    title: "System variables",
    subtitle: "Engine-injected on every run. {{sys.<key>}}",
    tip: "Read-only — no wiring needed.",
    refPrefix: "sys",
    editable: false,
  },
};

const SCHEMA_TYPES = ["string", "number", "integer", "boolean", "object", "array"] as const;
type SchemaTypeKey = (typeof SCHEMA_TYPES)[number];

interface WorkflowVariablesPanelProps {
  kind: VariableKind;
  items: WorkflowVariable[];
  onChange: (next: WorkflowVariable[]) => void;
  /** Required when kind === "system"; ignored otherwise. Sourced from /api/workflows/system-variables. */
  systemVariables?: ReadonlyArray<SystemVariableDto>;
}

export function WorkflowVariablesPanel({ kind, items, onChange, systemVariables }: WorkflowVariablesPanelProps) {
  const meta = KIND_LABELS[kind];

  if (!meta.editable) {
    // System: read-only. Render the canonical list fetched from the backend. While the
    // query is in-flight we render the empty list — the parent's useSystemVariables hook
    // caches with staleTime: Infinity, so this only flickers once on a fresh page load.
    const sysList = systemVariables ?? [];
    return (
      <div className="wf-vars-panel">
        <header className="wf-vars-panel-head">
          <div className="wf-vars-panel-titlebox">
            <h3 className="wf-vars-panel-title">{meta.title}</h3>
            <p className="wf-vars-panel-subtitle">{meta.subtitle}</p>
          </div>
        </header>

        <div className="wf-vars-panel-tip" role="note">
          <strong>TIPS</strong>
          <span>{meta.tip}</span>
        </div>

        {sysList.length === 0 && <div className="wf-vars-panel-empty">Loading…</div>}

        {sysList.length > 0 && (
          <ul className="wf-vars-list">
            {sysList.map((sv) => (
              <li key={sv.key} className="wf-vars-row wf-vars-row-readonly">
                <div className="wf-vars-row-h">
                  <Ic.Box size={12} />
                  <span className="wf-vars-row-name-static">sys.{sv.key}</span>
                  <span className="wf-vars-row-type-static">{sv.type}</span>
                </div>
                <p className="wf-vars-row-desc">{sv.description}</p>
                <code
                  className="wf-vars-row-ref"
                  title="Click to copy"
                  onClick={() => navigator.clipboard?.writeText(`{{sys.${sv.key}}}`)}
                >{`{{sys.${sv.key}}}`}</code>
              </li>
            ))}
          </ul>
        )}
      </div>
    );
  }

  // Editable scopes: inputs / outputs.
  const update = (index: number, patch: Partial<WorkflowVariable>) => {
    const next = items.map((v, i) => (i === index ? { ...v, ...patch } : v));
    onChange(next);
  };

  const remove = (index: number) => onChange(items.filter((_, i) => i !== index));

  const add = () => {
    const next: WorkflowVariable = {
      name: nextName(items),
      schema: { type: "string" } as unknown,
      required: kind === "inputs" ? false : undefined,
    };
    onChange([...items, next]);
  };

  return (
    <div className="wf-vars-panel">
      <header className="wf-vars-panel-head">
        <div className="wf-vars-panel-titlebox">
          <h3 className="wf-vars-panel-title">{meta.title}</h3>
          <p className="wf-vars-panel-subtitle">{meta.subtitle}</p>
        </div>
        <button type="button" className="btn btn-ghost wf-vars-panel-add" onClick={add}>
          <Ic.Plus size={12} /> {meta.addLabel}
        </button>
      </header>

      <div className="wf-vars-panel-tip" role="note">
        <strong>TIPS</strong>
        <span>{meta.tip}</span>
      </div>

      {items.length === 0 && <div className="wf-vars-panel-empty">{meta.emptyHint}</div>}

      {items.length > 0 && (
        <ul className="wf-vars-list">
          {items.map((v, i) => (
            <VariableRow
              key={i}
              kind={kind}
              refPrefix={meta.refPrefix}
              value={v}
              onChange={(patch) => update(i, patch)}
              onRemove={() => remove(i)}
            />
          ))}
        </ul>
      )}
    </div>
  );
}

interface VariableRowProps {
  kind: VariableKind;
  refPrefix: string;
  value: WorkflowVariable;
  onChange: (patch: Partial<WorkflowVariable>) => void;
  onRemove: () => void;
}

function VariableRow({ kind, refPrefix, value, onChange, onRemove }: VariableRowProps) {
  const [expanded, setExpanded] = useState(false);

  const schemaType = extractSchemaType(value.schema);

  const changeType = (next: SchemaTypeKey) => {
    onChange({ schema: { type: next } as unknown });
  };

  return (
    <li className="wf-vars-row" data-expanded={expanded}>
      <div className="wf-vars-row-h">
        <input
          className="wf-form-input wf-vars-row-name"
          value={value.name}
          onChange={(e) => onChange({ name: e.target.value })}
          placeholder="name"
        />
        <select
          className="wf-form-input wf-vars-row-type"
          value={schemaType}
          onChange={(e) => changeType(e.target.value as SchemaTypeKey)}
        >
          {SCHEMA_TYPES.map((t) => <option key={t} value={t}>{t}</option>)}
        </select>
        {kind === "inputs" && (
          <label className="wf-vars-row-required" title="Required at run time">
            <input
              type="checkbox"
              checked={value.required ?? false}
              onChange={(e) => onChange({ required: e.target.checked })}
            />
            <span>required</span>
          </label>
        )}
        <button
          type="button"
          className="btn btn-ghost wf-vars-row-toggle"
          onClick={() => setExpanded((v) => !v)}
          title={expanded ? "Hide details" : "Show details"}
        >
          {expanded ? <Ic.ChevronDown size={11} /> : <Ic.ChevronRight size={11} />}
        </button>
        <button type="button" className="btn btn-ghost wf-vars-row-remove" onClick={onRemove} title="Remove">
          <Ic.Trash size={11} />
        </button>
      </div>

      <code className="wf-vars-row-ref" title="Click to copy" onClick={() => navigator.clipboard?.writeText(`{{${refPrefix}.${value.name}}}`)}>
        {`{{${refPrefix}.${value.name}}}`}
      </code>

      {expanded && (
        <div className="wf-vars-row-detail">
          <label className="wf-form-row">
            <span className="wf-form-label">Label</span>
            <input
              className="wf-form-input"
              value={value.label ?? ""}
              onChange={(e) => onChange({ label: e.target.value || null })}
              placeholder={value.name}
            />
          </label>

          <label className="wf-form-row">
            <span className="wf-form-label">Description</span>
            <input
              className="wf-form-input"
              value={value.description ?? ""}
              onChange={(e) => onChange({ description: e.target.value || null })}
              placeholder="One-line hint shown to operators"
            />
          </label>

          {kind !== "outputs" && (
            <label className="wf-form-row">
              <span className="wf-form-label">Default value</span>
              <input
                className="wf-form-input"
                value={defaultToString(value.default)}
                onChange={(e) => onChange({ default: parseDefault(schemaType, e.target.value) })}
                placeholder={kind === "inputs" ? "Optional — applied when caller omits this input" : "Optional"}
              />
              <span className="wf-form-help">Stored as JSON. For object/array values use the raw JSON syntax.</span>
            </label>
          )}
        </div>
      )}
    </li>
  );
}

// ─── Helpers ────────────────────────────────────────────────────────────────────

function extractSchemaType(schema: unknown): SchemaTypeKey {
  if (typeof schema !== "object" || schema == null) return "string";
  const t = (schema as { type?: string | string[] }).type;
  if (typeof t === "string" && (SCHEMA_TYPES as readonly string[]).includes(t)) return t as SchemaTypeKey;
  return "string";
}

function defaultToString(value: unknown): string {
  if (value == null) return "";
  if (typeof value === "string") return value;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return JSON.stringify(value);
}

function parseDefault(type: SchemaTypeKey, raw: string): unknown {
  if (raw === "") return undefined;
  if (type === "string") return raw;
  if (type === "boolean") return raw === "true";
  if (type === "number" || type === "integer") {
    const n = Number(raw);
    return Number.isFinite(n) ? n : raw;
  }
  try { return JSON.parse(raw); }
  catch { return raw; }
}

function nextName(items: WorkflowVariable[]): string {
  const taken = new Set(items.map((i) => i.name));
  let i = 1;
  while (taken.has(`var${i}`)) i++;
  return `var${i}`;
}
