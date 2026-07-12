import { useId, useMemo, useRef, useState, type ReactNode } from "react";

import { Ic } from "@/_imported/ai-code-space/icons";
import { coerceNumberInput } from "@/lib/inputFieldSchema";

import type { ScopeSuggestion } from "./scope-introspection";
import { AgentMultiSelector, AgentSelector } from "./selectors/AgentSelector";
import { ConversationSelector } from "./selectors/ConversationSelector";
import { CredentialedModelMultiSelector, CredentialedModelSelector } from "./selectors/CredentialedModelSelector";
import { HarnessSelector } from "./selectors/HarnessSelector";
import { ModelCredentialSelector } from "./selectors/ModelCredentialSelector";
import { ProjectRepositorySelector } from "./selectors/ProjectRepositorySelector";
import { RelatedRepositoriesEditor } from "./selectors/RelatedRepositoriesEditor";
import { TriggerRepositoriesSelector } from "./selectors/TriggerRepositoriesSelector";
import { ActAsUserSelector } from "./selectors/ActAsUserSelector";
import { UserMultiSelector, UserSelector } from "./selectors/UserSelector";
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
 *   - array of string → token chips (removable tags; items.enum feeds autocomplete)
 *   - array of object → a repeatable list of sub-forms (one per item), built from items.properties
 *   - object (with properties) → a nested sub-form, recursing into its declared properties
 *
 * Anything richer (oneOf/anyOf, or a free-form object WITHOUT declared properties) falls back to a
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
  /** Marks a long/multi-line string field (e.g. a "Paragraph" input) so it renders as a textarea. */
  "x-long"?: boolean;
  /**
   * Marks a secondary field so the form tucks it under a collapsed "Advanced" section instead of the main
   * list — keeps the common path clean (e.g. a button's key/label/style stay visible; requiresComment /
   * resolvesWait / vetoes move to Advanced). Purely presentational; the on-disk value shape is unchanged.
   */
  "x-advanced"?: boolean;
  /** Plain-language label that overrides the humanized property name (e.g. "resolve" → "Decision rule"). */
  title?: string;
  /** Friendly display text per enum value (e.g. {"first":"First response wins"}). The stored value is unchanged. */
  "x-enumLabels"?: Record<string, string>;
  /** One-line consequence per enum value, shown under the label on a radioCards control (e.g.
   *  {"1":"the run parks until you approve"}). Display-only; the stored value is unchanged. */
  "x-optionConsequence"?: Record<string, string>;
  /** Marks a property as an "interaction field" — a mutually-exclusive component slot. The
   * PostMessageInputsEditor reads this to build the interaction-type picker instead of showing
   * all interaction fields at once. Never read by SchemaForm itself. */
  "x-interactionField"?: boolean;
  /** Human-readable label for an x-interactionField option, shown in the type picker. */
  "x-interactionLabel"?: string;
  /** Conditional visibility: render this field ONLY when a SIBLING field (same object level) equals a
   * value — e.g. show `count` only when `mode` === "quorum". Hidden fields keep their stored value (the
   * engine ignores irrelevant ones), so toggling the condition is non-destructive. Purely presentational. */
  "x-showWhen"?: { field: string; equals: unknown };
  /** Buckets a field into a named section (e.g. "Guardrails"). When ANY field declares x-group, SchemaForm
   *  renders titled sections instead of one flat list; ungrouped fields fall into a trailing "More" section.
   *  Presentational only — the stored value is unchanged, and a node with no x-group renders exactly as before. */
  "x-group"?: string;
  /** Root-level only: the section order for the grouped layout (group names). Groups not listed are appended
   *  in first-seen order. Ignored when no field declares x-group. */
  "x-sections"?: string[];
  /**
   * Explicit control discriminator: pick WHICH widget renders this field, independent of its JSON type.
   * The SAME stored value can render as a different control — e.g. a closed enum as "segmented" (a lifted
   * button-group) instead of the default &lt;select&gt;. renderControl reads this FIRST; when the value is
   * absent OR unrecognised for the field's shape it falls through to today's x-selector / enum / type
   * dispatch, so this is purely additive and non-breaking (the on-disk value shape is unchanged).
   * Known values: "segmented", "stepper", "radioCards".
   */
  "x-control"?: string;
  /** Short unit shown after a stepper's value (e.g. "min", "sec", "×"). Display-only; the stored value is
   *  the bare number. */
  "x-unit"?: string;
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

  const renderField = ([key, propSchema]: [string, Schema]) => (
    <Field
      key={key}
      name={key}
      required={required.has(key)}
      schema={propSchema}
      value={obj[key]}
      onChange={(next) => update(key, next)}
      templateHint={templateHint}
      variableSuggestions={variableSuggestions}
      siblings={obj}
    />
  );

  // x-showWhen: drop a field whose sibling condition isn't met (e.g. `count` only when mode === "quorum")
  // so the form shows only what's relevant. Then x-advanced fields collapse under "Advanced" so the
  // common path stays light; everything else renders inline, in declared order.
  const showWhenMet = (s: Schema) => {
    const cond = s["x-showWhen"];
    return !cond || (obj as Record<string, unknown>)[cond.field] === cond.equals;
  };
  const entries = Object.entries(parsed.properties).filter(([, s]) => showWhenMet(s));

  // A field's x-advanced tucks it under an "Advanced" drawer; a field's x-group buckets it into a titled
  // section. When NO field declares a group we keep the flat layout (primary inline + one Advanced drawer),
  // so every existing node renders byte-for-byte as before.
  type Entry = [string, Schema];
  const split = (es: Entry[]) => ({ primary: es.filter(([, s]) => !s["x-advanced"]), advanced: es.filter(([, s]) => s["x-advanced"]) });
  const advancedDrawer = (advanced: Entry[]) =>
    advanced.length > 0 && (
      <details className="wf-form-advanced">
        <summary className="wf-form-advanced-summary">Advanced</summary>
        <div className="wf-form-advanced-body">{advanced.map(renderField)}</div>
      </details>
    );

  if (!entries.some(([, s]) => s["x-group"])) {
    const { primary, advanced } = split(entries);
    return (
      <div className="wf-form">
        {primary.map(renderField)}
        {advancedDrawer(advanced)}
      </div>
    );
  }

  // Grouped layout: bucket by x-group (ungrouped → a trailing "More"), order the sections by the root
  // x-sections list (then any remaining groups in first-seen order), each section with its own Advanced drawer.
  const byGroup = new Map<string, Entry[]>();
  for (const e of entries) {
    const g = e[1]["x-group"] ?? "More";
    const bucket = byGroup.get(g);
    if (bucket) bucket.push(e);
    else byGroup.set(g, [e]);
  }
  const declared = parsed["x-sections"] ?? [];
  const orderedNames = [...declared.filter((g) => byGroup.has(g)), ...[...byGroup.keys()].filter((g) => !declared.includes(g))];

  return (
    <div className="wf-form">
      {orderedNames.map((name) => {
        const { primary, advanced } = split(byGroup.get(name)!);
        return (
          <div className="wf-form-group" key={name}>
            <div className="wf-form-group-h">{name}</div>
            {primary.map(renderField)}
            {advancedDrawer(advanced)}
          </div>
        );
      })}
    </div>
  );
}

function Field({ name, required, schema, value, onChange, templateHint, variableSuggestions, siblings }: {
  name: string;
  required: boolean;
  schema: Schema;
  value: unknown;
  onChange: (next: unknown) => void;
  templateHint: boolean;
  variableSuggestions?: ScopeSuggestion[];
  /** The other field values at this object level — lets a control read a sibling (e.g. the repositoryId a
   *  user picker filters by). Generic + optional; a control that doesn't need siblings ignores it. */
  siblings?: Record<string, unknown>;
}) {
  const label = schema.title ?? humanize(name);
  const description = schema.description;

  // Boolean → an inline checkbox-BEFORE-label row, not the label-on-top stack the other types use
  // (which leaves the checkbox dangling on its own line). A <label> wrapper is safe + desirable here:
  // a checkbox IS labelable, there's no contenteditable/@-button inside to mis-forward to, and it lets
  // the user click the text to toggle. Description sits under the label so a long hint can wrap.
  if (schema.type === "boolean" && !schema.enum && !schema["x-selector"]) {
    return (
      <label className="wf-form-check wf-form-check-field">
        <input
          type="checkbox"
          className="wf-form-checkbox"
          checked={Boolean(value)}
          onChange={(e) => onChange(e.target.checked)}
        />
        <span className="wf-form-check-text">
          <span className="wf-form-check-label">
            {label}
            {required && <span className="wf-form-required">*</span>}
          </span>
          {description && <span className="wf-form-help">{description}</span>}
        </span>
      </label>
    );
  }

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
      {renderControl(schema, value, onChange, templateHint, variableSuggestions, siblings)}
      {description && <span className="wf-form-help">{description}</span>}
    </div>
  );
}

/**
 * Shared radiogroup keyboard + roving-tabindex behaviour for the enum controls that present options as
 * buttons (Segmented, RadioCards). Arrows move the selection (wrapping), Home/End jump to the ends, a
 * roving tabindex keeps the group a single tab stop, and focus follows selection.
 */
function useRovingRadio(options: string[], current: string, onSelect: (value: string) => void) {
  const activeIdx = options.indexOf(current);
  const refs = useRef<(HTMLButtonElement | null)[]>([]);
  const select = (i: number) => { onSelect(options[i]); refs.current[i]?.focus(); };
  const onKeyDown = (e: React.KeyboardEvent) => {
    if (options.length === 0) return;
    const from = activeIdx < 0 ? 0 : activeIdx;
    if (e.key === "ArrowRight" || e.key === "ArrowDown") { e.preventDefault(); select((from + 1) % options.length); }
    else if (e.key === "ArrowLeft" || e.key === "ArrowUp") { e.preventDefault(); select((from - 1 + options.length) % options.length); }
    else if (e.key === "Home") { e.preventDefault(); select(0); }
    else if (e.key === "End") { e.preventDefault(); select(options.length - 1); }
  };
  const tabIndex = (i: number) => (options[i] === current || (activeIdx < 0 && i === 0) ? 0 : -1);
  const setRef = (i: number) => (el: HTMLButtonElement | null) => { refs.current[i] = el; };
  return { onKeyDown, tabIndex, setRef };
}

/**
 * A themed button-group for a short closed enum — a glanceable, one-click alternative to the
 * &lt;select&gt; for fields that declare "x-control": "segmented". It stores the RAW enum value as a
 * string (identical to the &lt;select&gt; path's `e.target.value`), so it is a purely visual swap and
 * fully non-breaking. Option text comes from x-enumLabels when present.
 */
function SegmentedControl({ schema, value, onChange }: { schema: Schema; value: unknown; onChange: (next: unknown) => void }) {
  const enumLabels = schema["x-enumLabels"] ?? {};
  const options = (schema.enum ?? []).map((v) => String(v));
  const current = value == null ? "" : String(value);
  const { onKeyDown, tabIndex, setRef } = useRovingRadio(options, current, onChange);

  return (
    <div className="wf-segmented" role="radiogroup" onKeyDown={onKeyDown}>
      {options.map((key, i) => (
        <button
          key={key}
          ref={setRef(i)}
          type="button"
          role="radio"
          aria-checked={current === key}
          tabIndex={tabIndex(i)}
          className="wf-segmented-opt"
          data-active={current === key || undefined}
          onClick={() => onChange(key)}
        >
          {enumLabels[key] ?? key}
        </button>
      ))}
    </div>
  );
}

/**
 * A short closed enum as stacked full-width cards, one per option, each showing its friendly label
 * (x-enumLabels) and a one-line consequence (x-optionConsequence) — for a fork in behaviour a reader must
 * understand before choosing (review mode, decision type, on-failure policy). Stores the RAW enum value as
 * a string (identical to the &lt;select&gt; path), so it is a purely visual swap and fully non-breaking.
 */
function RadioCardsControl({ schema, value, onChange }: { schema: Schema; value: unknown; onChange: (next: unknown) => void }) {
  const enumLabels = schema["x-enumLabels"] ?? {};
  const consequences = schema["x-optionConsequence"] ?? {};
  const options = (schema.enum ?? []).map((v) => String(v));
  const current = value == null ? "" : String(value);
  const { onKeyDown, tabIndex, setRef } = useRovingRadio(options, current, onChange);

  return (
    <div className="wf-radiocards" role="radiogroup" onKeyDown={onKeyDown}>
      {options.map((key, i) => (
        <button
          key={key}
          ref={setRef(i)}
          type="button"
          role="radio"
          aria-checked={current === key}
          tabIndex={tabIndex(i)}
          className="wf-radiocard"
          data-active={current === key || undefined}
          onClick={() => onChange(key)}
        >
          <span className="wf-radiocard-dot" aria-hidden="true" />
          <span className="wf-radiocard-body">
            <span className="wf-radiocard-label">{enumLabels[key] ?? key}</span>
            {consequences[key] && <span className="wf-radiocard-desc">{consequences[key]}</span>}
          </span>
        </button>
      ))}
    </div>
  );
}

/**
 * A bounded integer/number as a −/+ stepper for fields that declare "x-control": "stepper". Clamps to the
 * schema's min/max, shows the effective default as placeholder when empty (so a defaulted knob never reads
 * as a required blank), and renders an optional x-unit. Stores a bare JSON number (identical to the plain
 * number input) — or undefined when cleared. A value that is already a dynamic {{ref}} stays editable via
 * the picker input so it is never stranded.
 */
function StepperControl({ schema, value, onChange, variableSuggestions }: { schema: Schema; value: unknown; onChange: (next: unknown) => void; variableSuggestions?: ScopeSuggestion[] }) {
  const min = schema.minimum;
  const max = schema.maximum;
  const unit = schema["x-unit"];
  const def = typeof schema.default === "number" ? schema.default : undefined;

  if (typeof value === "string" && value.includes("{{") && variableSuggestions && variableSuggestions.length > 0) {
    return (
      <VariablePickerInput
        value={value}
        onChange={(next) => onChange(coerceNumberInput(next))}
        suggestions={variableSuggestions}
        placeholder="Type @ to insert a value"
      />
    );
  }

  const clamp = (n: number) => Math.min(max ?? Infinity, Math.max(min ?? -Infinity, n));
  const num = typeof value === "number" ? value
    : (typeof value === "string" && value.trim() !== "" && !Number.isNaN(Number(value)) ? Number(value) : undefined);
  const stepBy = (delta: number) => onChange(clamp((num ?? def ?? min ?? 0) + delta));
  const atMin = num != null && min != null && num <= min;
  const atMax = num != null && max != null && num >= max;
  const placeholder = def != null ? String(def) : (min != null ? String(min) : "");   // unit shows in its own span

  return (
    <div className="wf-stepper">
      <button type="button" className="wf-stepper-btn" aria-label="Decrease" disabled={atMin} onClick={() => stepBy(-1)}>&minus;</button>
      <input
        className="wf-stepper-val"
        type="text"
        inputMode="numeric"
        value={num != null ? String(num) : ""}
        placeholder={placeholder}
        onChange={(e) => {
          const v = e.target.value.trim();
          if (v === "") { onChange(undefined); return; }
          const n = Number(v);
          if (!Number.isNaN(n)) onChange(clamp(n));
        }}
      />
      {unit && <span className="wf-stepper-unit">{unit}</span>}
      <button type="button" className="wf-stepper-btn" aria-label="Increase" disabled={atMax} onClick={() => stepBy(1)}>+</button>
    </div>
  );
}

/**
 * A token/tag input for an array of strings — each entry is a removable chip and a trailing input adds
 * more (Enter or comma commits; Backspace on an empty input removes the last; paste splits on commas /
 * newlines). Replaces the comma-separated text box so the author sees and edits each item. When
 * items.enum is present the input offers those values as native autocomplete. Stores the same string[].
 */
function ChipsControl({ schema, value, onChange }: { schema: Schema; value: unknown; onChange: (next: unknown) => void }) {
  const arr = Array.isArray(value) ? (value as unknown[]).map(String) : [];
  const [draft, setDraft] = useState("");
  const listId = useId();
  const options = Array.isArray(schema.items?.enum) ? schema.items!.enum!.map(String) : undefined;

  const commit = (raw: string) => {
    const parts = raw.split(/[,\n]/).map((s) => s.trim()).filter((s) => s.length > 0 && !arr.includes(s));
    if (parts.length > 0) onChange([...arr, ...parts]);
    setDraft("");
  };
  const removeAt = (i: number) => onChange(arr.filter((_, j) => j !== i));

  return (
    <div className="wf-chips">
      {arr.map((tok, i) => (
        <span key={`${tok}-${i}`} className="wf-chip">
          {tok}
          <button type="button" className="wf-chip-x" aria-label={`Remove ${tok}`} onClick={() => removeAt(i)}>&times;</button>
        </span>
      ))}
      <input
        className="wf-chip-input"
        type="text"
        value={draft}
        placeholder={arr.length === 0 ? "Add and press Enter…" : ""}
        list={options ? listId : undefined}
        onChange={(e) => { const v = e.target.value; if (v.endsWith(",")) commit(v); else setDraft(v); }}
        onKeyDown={(e) => {
          if (e.key === "Enter" && draft.trim()) { e.preventDefault(); commit(draft); }
          else if (e.key === "Backspace" && draft === "" && arr.length > 0) { removeAt(arr.length - 1); }
        }}
        onBlur={() => { if (draft.trim()) commit(draft); }}
      />
      {options && (
        <datalist id={listId}>
          {options.filter((o) => !arr.includes(o)).map((o) => <option key={o} value={o} />)}
        </datalist>
      )}
    </div>
  );
}

function renderControl(schema: Schema, value: unknown, onChange: (next: unknown) => void, templateHint: boolean, variableSuggestions?: ScopeSuggestion[], siblings?: Record<string, unknown>) {
  // x-control — an explicit widget discriminator, read FIRST. When a property declares a recognised
  // "x-control" for its shape we render that widget for the SAME stored value (e.g. an enum as a
  // segmented button-group). Absent OR unrecognised falls through to the x-selector / enum / type
  // dispatch below, so this is additive and non-breaking. A segmented enum keeps the same Pick ⇄
  // Expression affordance the <select> gets, so it can still be bound to a dynamic {{ref}}.
  if (schema["x-control"] === "segmented" && Array.isArray(schema.enum)) {
    const segmented = <SegmentedControl schema={schema} value={value} onChange={onChange} />;
    return variableSuggestions != null && variableSuggestions.length > 0
      ? <DualModeSelector pick={segmented} value={value} onChange={onChange} variableSuggestions={variableSuggestions} />
      : segmented;
  }
  if (schema["x-control"] === "stepper" && (schema.type === "integer" || schema.type === "number")) {
    return <StepperControl schema={schema} value={value} onChange={onChange} variableSuggestions={variableSuggestions} />;
  }
  if (schema["x-control"] === "radioCards" && Array.isArray(schema.enum)) {
    return <RadioCardsControl schema={schema} value={value} onChange={onChange} />;
  }

  // Custom selector takes the highest precedence — when a property carries
  // "x-selector": "<key>" we hand off to renderCustomSelector. The on-disk value
  // stays the schema-declared shape (string for repository, etc.).
  const selectorKey = schema["x-selector"];
  if (selectorKey) {
    const custom = renderCustomSelector(selectorKey, schema, value, onChange, siblings);
    if (custom != null) {
      // A scalar-string selector (e.g. repository, conversation) can ALSO be bound to a dynamic
      // {{ }} reference — essential when the target isn't known at design time. When the editor
      // offers in-scope variables, wrap it in a Pick ⇄ Expression toggle so the author can switch
      // to an @-reference. Array selectors (type !== string, e.g. trigger.repositories) and the run
      // form (no suggestions) keep just the picker. The stored value is a string either way.
      const dynamic = schema.type === "string" && variableSuggestions != null && variableSuggestions.length > 0;
      return dynamic
        ? <DualModeSelector pick={custom} value={value} onChange={onChange} variableSuggestions={variableSuggestions} />
        : custom;
    }
    // Unknown selector key — fall through to the type-based renderer so the field
    // still functions (operator can hand-type the value) and we don't strand
    // unsupported manifests behind a missing component.
  }

  // Enum first — applies regardless of type. Option text comes from x-enumLabels when present (friendly
  // wording); the stored value is always the raw enum value, so this is purely a display nicety.
  if (schema.enum) {
    const enumLabels = schema["x-enumLabels"] ?? {};
    const select = (
      <select
        className="wf-form-input"
        value={(value as string) ?? ""}
        onChange={(e) => onChange(e.target.value)}
      >
        {!schema.enum.includes(value) && <option value="">— select —</option>}
        {schema.enum.map((v) => (
          <option key={String(v)} value={String(v)}>{enumLabels[String(v)] ?? String(v)}</option>
        ))}
      </select>
    );

    // In the editor (variable suggestions present) an enum gets the same Pick ⇄ Expression toggle as a
    // string selector, so its value can ALSO be a dynamic {{ref}} — e.g. git.pr_review.verdict bound to a
    // chat card's clicked action. Stored value is a string either way (a literal option or a {{ref}}), so
    // it's non-breaking and the engine resolves it like any input. The run form (no suggestions) keeps the
    // plain dropdown for fast literal entry.
    return variableSuggestions != null && variableSuggestions.length > 0
      ? <DualModeSelector pick={select} value={value} onChange={onChange} variableSuggestions={variableSuggestions} />
      : select;
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
    // In the editor (suggestions present) a number field is picker-capable, so it can reference
    // an input / upstream value via @ or {{ }} — `coerceNumberInput` keeps literals as JSON
    // numbers and {{ref}} templates as strings (a lone ref is type-preserved at run time). With
    // no suggestions (e.g. the run form) it stays a plain numeric input for fast literal entry.
    if (variableSuggestions && variableSuggestions.length > 0) {
      return (
        <VariablePickerInput
          value={value == null ? "" : String(value)}
          onChange={(next) => onChange(coerceNumberInput(next))}
          suggestions={variableSuggestions}
          expectType={type}
          placeholder="Type @ to insert an input or step output"
        />
      );
    }
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
    // Use a textarea for "long" string fields — an explicit `x-long` marker (the "Paragraph"
    // input type) or the legacy heuristic of minLength > 100. Everything else is a single line.
    const isLong = schema["x-long"] === true || (schema.minLength != null && schema.minLength > 100);
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
          placeholder={variableSuggestions && variableSuggestions.length > 0 ? "Type @ to insert an input or step output" : undefined}
        />
        {templateHint && variableSuggestions && variableSuggestions.length > 0 && <TemplateHint />}
      </>
    );
  }

  if (type === "array" && schema.items?.type === "string") {
    return <ChipsControl schema={schema} value={value} onChange={onChange} />;
  }

  if (type === "array" && schema.items?.type === "object" && schema.items.properties) {
    return (
      <ObjectArrayEditor
        itemSchema={schema.items}
        value={value}
        onChange={onChange}
        templateHint={templateHint}
        variableSuggestions={variableSuggestions}
      />
    );
  }

  if (type === "object" && schema.properties && Object.keys(schema.properties).length > 0) {
    // Structured nested object → a recursive sub-form (one control per nested property), visually
    // grouped. Driven purely by schema, so e.g. chat.post_message's `resolve { mode, count }` renders
    // as a select + number — not the raw-JSON fallback. A free-form object (no declared `properties`,
    // e.g. a form's `fields` JSON Schema) has nothing to recurse into and still falls through to JSON.
    return (
      <div className="wf-form-nested">
        <SchemaForm
          schema={schema}
          value={value}
          onChange={(next) => onChange(next)}
          templateHint={templateHint}
          variableSuggestions={variableSuggestions}
        />
      </div>
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
function renderCustomSelector(key: string, schema: Schema, value: unknown, onChange: (next: unknown) => void, siblings?: Record<string, unknown>) {
  switch (key) {
    case "user":
      // Array field → a multi-member toggle list (e.g. allowedResponderUserIds); scalar → a single-user
      // select (which SchemaForm also wraps in the Pick ⇄ Expression toggle). Generic to any user-id field.
      return schema.type === "array"
        ? <UserMultiSelector value={Array.isArray(value) ? (value as string[]) : []} onChange={(next) => onChange(next.length === 0 ? undefined : next)} />
        : <UserSelector value={typeof value === "string" ? value : ""} onChange={(next) => onChange(next === "" ? undefined : next)} />;
    case "actorUser":
      // "Act as" author picker: only teammates with a live linked identity on the SIBLING repositoryId's
      // provider (so a picked author's write can't fail). Filtered via the sibling repository; stores the
      // same user-id string, and is still wrapped in the Pick ⇄ Expression toggle by renderControl.
      return (
        <ActAsUserSelector
          repositoryId={typeof siblings?.repositoryId === "string" ? siblings.repositoryId : undefined}
          value={typeof value === "string" ? value : ""}
          onChange={(next) => onChange(next === "" ? undefined : next)}
        />
      );
    case "repository":
      return (
        <ProjectRepositorySelector
          value={typeof value === "string" ? value : ""}
          onChange={(next) => onChange(next === "" ? undefined : next)}
        />
      );
    case "conversation":
      return (
        <ConversationSelector
          value={typeof value === "string" ? value : ""}
          onChange={(next) => onChange(next === "" ? undefined : next)}
        />
      );
    case "agent":
      // Array field → multi-persona chips (e.g. the supervisor's allowedAgentDefinitionIds); scalar → single.
      return schema.type === "array"
        ? <AgentMultiSelector value={Array.isArray(value) ? (value as string[]) : []} onChange={(next) => onChange(next.length === 0 ? undefined : next)} />
        : <AgentSelector value={typeof value === "string" ? value : ""} onChange={(next) => onChange(next === "" ? undefined : next)} />;
    case "harness":
      return (
        <HarnessSelector
          value={typeof value === "string" ? value : ""}
          onChange={(next) => onChange(next === "" ? undefined : next)}
        />
      );
    case "credentialedModel":
      // Value = the credentialed-model ROW id (the pool resolves it via ResolveByRowIdAsync — NOT the bare
      // model id or the credential id). Array field → multi-select chips (e.g. the supervisor's
      // allowedModelIds); scalar → a single picker (which SchemaForm also wraps in Pick ⇄ Expression).
      return schema.type === "array"
        ? <CredentialedModelMultiSelector value={Array.isArray(value) ? (value as string[]) : []} onChange={(next) => onChange(next.length === 0 ? undefined : next)} />
        : <CredentialedModelSelector value={typeof value === "string" ? value : ""} onChange={(next) => onChange(next === "" ? undefined : next)} />;
    case "modelCredential":
      // Value = the owning ModelCredential id (not a model row). Scalar in current manifests; harness-provider
      // filtering belongs to the agent-profile composite (which has the sibling harness), not this generic case.
      return (
        <ModelCredentialSelector
          value={typeof value === "string" ? value : ""}
          onChange={(next) => onChange(next === "" ? undefined : next)}
        />
      );
    case "relatedRepositories":
      // The { repositoryId, alias?, access }[] multi-repo editor — a project→repo cascade per row with an
      // auto alias, instead of the generic array editor's raw repo-id text boxes. Handles the array shape
      // itself; empty ⇒ undefined so the key drops (single-repo byte-identical).
      return <RelatedRepositoriesEditor value={value} onChange={(next) => onChange(next)} />;
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

/**
 * Wraps a scalar-string custom selector with a Pick ⇄ Expression toggle. Pick = the dropdown
 * (static literal value); Expression = the @/{{ }} variable input (a dynamic reference resolved at
 * run time). The stored value is a plain string in both modes — a literal UUID or a `{{ref}}`
 * template — so this is non-breaking and the engine resolves the template like any other input.
 * Opening mode is inferred from the value (a `{{` ⇒ expression); the author can flip it freely.
 */
function DualModeSelector({ pick, value, onChange, variableSuggestions }: {
  pick: ReactNode;
  value: unknown;
  onChange: (next: unknown) => void;
  variableSuggestions: ScopeSuggestion[];
}) {
  const stringValue = typeof value === "string" ? value : "";
  const [mode, setMode] = useState<"pick" | "expr">(stringValue.includes("{{") ? "expr" : "pick");

  return (
    <div className="wf-dualmode">
      <div className="wf-dualmode-head" role="group" aria-label="Value mode">
        <button type="button" className="wf-dualmode-toggle" data-active={mode === "pick"} onClick={() => setMode("pick")}>Pick</button>
        <button type="button" className="wf-dualmode-toggle" data-active={mode === "expr"} onClick={() => setMode("expr")}>Expression</button>
      </div>
      {mode === "pick" ? pick : (
        <VariablePickerInput
          value={stringValue}
          onChange={(next) => onChange(next === "" ? undefined : next)}
          suggestions={variableSuggestions}
          placeholder="Type @ to reference an input or step output"
        />
      )}
    </div>
  );
}

/**
 * Generic editor for an `array` of `object` items: a repeatable list where each row is a sub-form
 * built recursively from the item schema's properties. Add / remove rows; the value stays a plain
 * array of objects. Driven purely by schema — no per-node knowledge — so any object-array input
 * (e.g. a row of action buttons) gets a usable editor instead of the raw-JSON fallback. Removing the
 * last row emits undefined (the field clears) so an empty list never lingers as a degenerate value.
 */
function ObjectArrayEditor({ itemSchema, value, onChange, templateHint, variableSuggestions }: {
  itemSchema: Schema;
  value: unknown;
  onChange: (next: unknown) => void;
  templateHint: boolean;
  variableSuggestions?: ScopeSuggestion[];
}) {
  const rows = Array.isArray(value) ? (value as Record<string, unknown>[]) : [];
  const replace = (next: Record<string, unknown>[]) => onChange(next.length === 0 ? undefined : next);

  return (
    <div className="wf-objarr">
      {rows.map((row, i) => (
        // Index key: rows aren't reorderable and each sub-form is fully value-controlled (props in,
        // changes out), so the index is stable enough for add / remove-by-index editing.
        <div className="wf-objarr-row" key={i}>
          <div className="wf-objarr-row-body">
            <SchemaForm
              schema={itemSchema}
              value={row}
              onChange={(next) => replace(rows.map((r, idx) => (idx === i ? next : r)))}
              templateHint={templateHint}
              variableSuggestions={variableSuggestions}
            />
          </div>
          <button type="button" className="btn btn-ghost btn-icon wf-objarr-remove" onClick={() => replace(rows.filter((_, idx) => idx !== i))} aria-label="Remove item">
            <Ic.Trash size={14} />
          </button>
        </div>
      ))}
      <button type="button" className="btn btn-ghost wf-objarr-add" onClick={() => replace([...rows, {}])}>
        <Ic.Plus size={14} /> Add item
      </button>
    </div>
  );
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
