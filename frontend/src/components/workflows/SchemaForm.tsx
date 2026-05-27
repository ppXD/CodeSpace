import { useMemo } from "react";

import type { ScopeSuggestion } from "./scope-introspection";
import { RepositorySelector } from "./selectors/RepositorySelector";
import { TriggerRepositoriesSelector } from "./selectors/TriggerRepositoriesSelector";
import { VariablePickerInput } from "./VariablePickerInput";

/**
 * Schema-driven form. Takes a JSON Schema object (the subset the engine emits — type,
 * properties, required, enum, default, description) and a value, renders one input per
 * property and pushes changes back via onChange.
 *
 * Generic on purpose: the editor never has hard-coded UI for any specific node type.
 * A new node type ships its manifest's ConfigSchema + InputSchema and the editor renders
 * a usable form automatically. This is the contract that lets plugin authors add nodes
 * without ever touching frontend code.
 *
 * Supported property shapes:
 *   - string  → text input or textarea (hint: {{path.to.value}} works)
 *   - number  → number input
 *   - integer → number input (step=1)
 *   - boolean → checkbox
 *   - enum    → select
 *   - array of string → comma-separated text (one line per entry on Enter)
 *
 * Anything richer (oneOf/anyOf, nested objects, arrays of objects) falls back to a
 * monospace JSON editor — operator-friendly escape hatch, not a dead end.
 */

interface Schema {
  type?: string;
  properties?: Record<string, Schema>;
  required?: string[];
  enum?: unknown[];
  default?: unknown;
  description?: string;
  items?: Schema;
  minimum?: number;
  maximum?: number;
  minLength?: number;
  format?: string;
  /**
   * Generic escape hatch: when a schema property declares <c>"x-selector": "&lt;key&gt;"</c>,
   * the form renders the matching custom selector instead of the default control. Keeps the
   * on-disk value shape (a plain string / UUID / number) — only the editing UX changes.
   * Backend ConfigSchema is the source of truth; new selectors plug in by adding a case
   * to <c>renderCustomSelector</c> below.
   */
  "x-selector"?: string;
}

interface SchemaFormProps {
  schema: unknown;
  value: unknown;
  onChange: (next: Record<string, unknown>) => void;
  /** Hint shown under string fields — "{{trigger.title}} resolves at run time…". */
  templateHint?: boolean;
  /**
   * When provided, every string/textarea field becomes a picker-aware input: typing `{{`
   * (or clicking the @ affordance) opens an autocomplete popover listing these variables.
   * Pass scope-introspection's output here to make node inputs reference any in-scope value
   * without typing the path by hand.
   */
  variableSuggestions?: ScopeSuggestion[];
}

export function SchemaForm({ schema, value, onChange, templateHint = false, variableSuggestions }: SchemaFormProps) {
  const parsed = useMemo(() => normalizeSchema(schema), [schema]);
  const obj = (typeof value === "object" && value !== null ? value : {}) as Record<string, unknown>;
  const required = new Set(parsed.required ?? []);

  if (!parsed.properties || Object.keys(parsed.properties).length === 0) {
    return <div className="wf-form-empty">No configuration.</div>;
  }

  const update = (key: string, next: unknown) => onChange({ ...obj, [key]: next });

  return (
    <div className="wf-form">
      {Object.entries(parsed.properties).map(([key, propSchema]) => (
        <Field
          key={key}
          name={key}
          required={required.has(key)}
          schema={propSchema}
          value={obj[key]}
          onChange={(next) => update(key, next)}
          templateHint={templateHint}
          variableSuggestions={variableSuggestions}
        />
      ))}
    </div>
  );
}

function Field({ name, required, schema, value, onChange, templateHint, variableSuggestions }: {
  name: string;
  required: boolean;
  schema: Schema;
  value: unknown;
  onChange: (next: unknown) => void;
  templateHint: boolean;
  variableSuggestions?: ScopeSuggestion[];
}) {
  const label = humanize(name);
  const description = schema.description;

  // IMPORTANT — must be a <div>, NOT a <label>. Native <label> elements forward any
  // click anywhere inside them to the first labelable control they contain. Our row
  // contains a contenteditable <div> (the picker input) and a <button> (the @ toolbar).
  // contenteditable is NOT labelable, so the browser forwards the click to the FIRST
  // <button> inside the label — which is the @ button — and the picker pops open as if
  // the user had clicked it. Using a <div> wrapper kills the auto-forwarding while
  // preserving the visual layout.
  return (
    <div className="wf-form-row">
      <span className="wf-form-label">
        {label}
        {required && <span className="wf-form-required">*</span>}
      </span>
      {renderControl(schema, value, onChange, templateHint, variableSuggestions)}
      {description && <span className="wf-form-help">{description}</span>}
    </div>
  );
}

function renderControl(schema: Schema, value: unknown, onChange: (next: unknown) => void, templateHint: boolean, variableSuggestions?: ScopeSuggestion[]) {
  // Custom selector takes the highest precedence — when a property carries
  // "x-selector": "<key>" we hand off to renderCustomSelector. The on-disk value
  // stays the schema-declared shape (string for repository, etc.).
  const selectorKey = schema["x-selector"];
  if (selectorKey) {
    const custom = renderCustomSelector(selectorKey, schema, value, onChange);
    if (custom != null) return custom;
    // Unknown selector key — fall through to the type-based renderer so the field
    // still functions (operator can hand-type the value) and we don't strand
    // unsupported manifests behind a missing component.
  }

  // Enum first — applies regardless of type.
  if (schema.enum) {
    return (
      <select
        className="wf-form-input"
        value={(value as string) ?? ""}
        onChange={(e) => onChange(e.target.value)}
      >
        {!schema.enum.includes(value) && <option value="">— select —</option>}
        {schema.enum.map((v) => (
          <option key={String(v)} value={String(v)}>{String(v)}</option>
        ))}
      </select>
    );
  }

  const type = schema.type;

  if (type === "boolean") {
    return (
      <input
        type="checkbox"
        className="wf-form-checkbox"
        checked={Boolean(value)}
        onChange={(e) => onChange(e.target.checked)}
      />
    );
  }

  if (type === "integer" || type === "number") {
    return (
      <input
        type="number"
        className="wf-form-input"
        step={type === "integer" ? 1 : "any"}
        min={schema.minimum}
        max={schema.maximum}
        value={value == null ? "" : String(value)}
        onChange={(e) => {
          const v = e.target.value;
          if (v === "") { onChange(undefined); return; }
          const n = Number(v);
          if (!Number.isNaN(n)) onChange(n);
        }}
      />
    );
  }

  if (type === "string") {
    // Use a textarea for "long" string fields — heuristic: name hints at prompt / body /
    // markdown content, or schema declared minLength > 100. Everything else is a single line.
    const isLong = schema.minLength != null && schema.minLength > 100;
    const stringValue = typeof value === "string" ? value : "";

    // Every string field gets the Dify-style framed input: copy + expand toolbar always,
    // @-picker added when in-scope suggestions exist. Passing an empty list still yields
    // the toolbar so the UX stays uniform across every inspector row.
    return (
      <>
        <VariablePickerInput
          value={stringValue}
          onChange={(next) => onChange(next)}
          suggestions={variableSuggestions ?? []}
          defaultMultiline={isLong}
        />
        {templateHint && variableSuggestions && variableSuggestions.length > 0 && <TemplateHint />}
      </>
    );
  }

  if (type === "array" && schema.items?.type === "string") {
    const arr = Array.isArray(value) ? value as string[] : [];
    return (
      <input
        type="text"
        className="wf-form-input"
        placeholder="comma-separated"
        value={arr.join(", ")}
        onChange={(e) => {
          const parts = e.target.value.split(",").map((s) => s.trim()).filter((s) => s.length > 0);
          onChange(parts);
        }}
      />
    );
  }

  // Fallback — raw JSON. Lets operators wire complex shapes (objects, $ref blocks) even when
  // the editor doesn't have a typed control for them. Round-trips safely.
  return (
    <>
      <textarea
        className="wf-form-textarea wf-form-textarea-mono"
        rows={4}
        value={value == null ? "" : JSON.stringify(value, null, 2)}
        onChange={(e) => {
          try {
            onChange(e.target.value === "" ? undefined : JSON.parse(e.target.value));
          } catch {
            // Keep the unparseable text in the textarea via local state? For now silently
            // ignore parse errors — the operator sees their text but the underlying value
            // doesn't update until JSON is valid. Tradeoff: simpler component.
          }
        }}
      />
      <span className="wf-form-help">Raw JSON.</span>
    </>
  );
}

/**
 * Dispatch table for the <c>x-selector</c> escape hatch. Each branch returns a JSX node
 * scoped to one selector key and is responsible for coercing the form value into the
 * selector's expected shape. Returning <c>null</c> means "no selector registered" — the
 * caller then falls back to the default control so the field still works.
 */
function renderCustomSelector(key: string, _schema: Schema, value: unknown, onChange: (next: unknown) => void) {
  switch (key) {
    case "repository":
      return (
        <RepositorySelector
          value={typeof value === "string" ? value : ""}
          onChange={(next) => onChange(next === "" ? undefined : next)}
        />
      );
    case "trigger.repositories":
      // List editor for the { repositoryId, labels? }[] shape used by PR-trigger
      // activation configs. The selector handles legacy { repositoryId, labels? }
      // shape transparently — see migrateLegacyTriggerConfig — so this branch can
      // accept any of the three shapes the matcher tolerates.
      return (
        <TriggerRepositoriesSelector
          value={value}
          onChange={(next) => onChange(next)}
        />
      );
    default:
      return null;
  }
}

function TemplateHint() {
  return (
    <span className="wf-form-help">
      Use <code>{"{{trigger.x}}"}</code> or <code>{"{{nodes.<id>.outputs.x}}"}</code> to reference upstream values.
    </span>
  );
}

function normalizeSchema(raw: unknown): Schema {
  if (typeof raw !== "object" || raw == null) return { properties: {} };
  return raw as Schema;
}

function humanize(name: string): string {
  // camelCase → "Camel Case", snake_case → "Snake Case"
  return name
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/_/g, " ")
    .replace(/^\w/, (c) => c.toUpperCase());
}
