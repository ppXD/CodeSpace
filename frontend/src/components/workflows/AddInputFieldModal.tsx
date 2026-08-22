import { useEffect, useMemo, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowVariable } from "@/api/workflows";
import {
  buildFieldSchema,
  INPUT_FIELD_TYPES,
  isFieldHidden,
  isSelectorFieldType,
  type InputFieldType,
  jsonTypeOf,
  schemaMaxLength,
  schemaOptions,
  schemaToFieldType,
} from "@/lib/inputFieldSchema";

interface AddInputFieldModalProps {
  /** When editing an existing field; omit to add a new one. */
  initial?: WorkflowVariable;
  /** Names already taken (for uniqueness validation); excludes the field being edited. */
  takenNames: string[];
  onSave: (field: WorkflowVariable) => void;
  onClose: () => void;
}

const NAME_RE = /^[A-Za-z_][A-Za-z0-9_]*$/;

/**
 * Dify-style "Add variable" dialog for a manual-start input field. Edits the friendly facets
 * (type / name / display name / options / max length / default / required / hidden) and emits a
 * {@link WorkflowVariable} whose `schema` is built by `buildFieldSchema`.
 *
 * Generic over field type: every entry in `INPUT_FIELD_TYPES` compiles to a JSON-schema shape
 * the engine + SchemaForm already understand, so adding a type is one array entry + (if it
 * needs a bespoke default control) one branch here. Warm-theme `.mdl` shell.
 */
export function AddInputFieldModal({ initial, takenNames, onSave, onClose }: AddInputFieldModalProps) {
  const [type, setType] = useState<InputFieldType>(initial ? schemaToFieldType(initial.schema) : "text");
  const [name, setName] = useState(initial?.name ?? "");
  const [displayName, setDisplayName] = useState(initial?.label ?? "");
  const [maxLength, setMaxLength] = useState<string>(() => {
    const m = initial ? schemaMaxLength(initial.schema) : null;
    return m != null ? String(m) : "";
  });
  const [options, setOptions] = useState<string[]>(() => (initial ? schemaOptions(initial.schema) : []));
  // Text/number/select share a string default box; boolean uses a tri-state select.
  // Whether the operator actually used a default control in this session. The editor's controls
  // cannot represent every default a workflow can hold -- the plan projector writes an array of
  // subtask objects, and `resolveDefault` answers `undefined` for anything it cannot round-trip -- so
  // writing its answer unconditionally erased those. Untouched means untouched: keep what is stored.
  const [defaultTouched, setDefaultTouched] = useState(false);
  const [defaultText, setDefaultText] = useState(() => (typeof initial?.default === "boolean" ? "" : defaultToString(initial?.default)));
  const [defaultBool, setDefaultBool] = useState<"" | "true" | "false">(() =>
    typeof initial?.default === "boolean" ? (initial.default ? "true" : "false") : "");
  const [required, setRequired] = useState(initial?.required ?? true);
  const [hidden, setHidden] = useState(initial ? isFieldHidden(initial.schema) : false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const usableOptions = useMemo(() => options.map((o) => o.trim()).filter((o) => o !== ""), [options]);

  const trimmedName = name.trim();
  const nameError = useMemo(() => {
    if (trimmedName === "") return "Required";
    if (!NAME_RE.test(trimmedName)) return "Letters, digits, underscore; can't start with a digit";
    if (takenNames.includes(trimmedName)) return "Already used";
    return null;
  }, [trimmedName, takenNames]);

  // A Select needs at least one option — without one it saves as a bare {type:string} (no enum) and reopens
  // as a plain Text field, silently dropping the operator's type choice. Require an option so the type survives.
  const optionsError = type === "select" && usableOptions.length === 0 ? "Add at least one option" : null;
  const canSave = nameError === null && optionsError === null;

  const markDefaultTouched = (next: string) => { setDefaultTouched(true); setDefaultText(next); };
  const markDefaultBoolTouched = (next: "" | "true" | "false") => { setDefaultTouched(true); setDefaultBool(next); };

  const save = () => {
    if (!canSave) return;
    const maxLen = maxLength.trim() === "" ? null : Number(maxLength);
    // Patch over the stored fragment, not a fresh object: a schema can carry keywords this modal has
    // no control for (a description, a format, an x-* a planner wrote) and rebuilding from {} deletes
    // every one of them.
    const storedSchema = typeof initial?.schema === "object" && initial.schema !== null ? (initial.schema as Record<string, unknown>) : undefined;
    const schema = buildFieldSchema({ type, maxLength: Number.isFinite(maxLen) ? maxLen : null, options: usableOptions, hidden }, storedSchema);

    onSave({
      // Spread first so a facet this modal does not edit -- today `description` -- survives being
      // edited through it. The named fields below are the ones it owns.
      ...initial,
      name: trimmedName,
      label: displayName.trim() === "" ? null : displayName.trim(),
      schema,
      // Only when the operator used a default control. Otherwise the stored default stands, whatever
      // shape it is -- these controls are strings and booleans, and a default can be an array of
      // objects a planner wrote.
      default: defaultTouched ? resolveDefault(type, defaultText, defaultBool, usableOptions) : initial?.default,
      required,
    });
  };

  return createPortal(
    <>
      <div className="mdl-mask" />
      <div className="mdl" role="dialog" aria-modal="true">
        <div className="mdl-head">
          <div className="mdl-title-wrap">
            <div className="mdl-title">{initial ? "Edit field" : "Add field"}</div>
            <div className="mdl-sub">Define an input collected when this workflow is run.</div>
          </div>
          <button className="mdl-x" onClick={onClose} title="Close"><Ic.X size={14} /></button>
        </div>

        <div className="mdl-body">
          <div className="wf-form">
            <div className="wf-form-row">
              <span className="wf-form-label">Field type</span>
              <div className="wf-type-pick">
                <select className="wf-form-input" value={type} onChange={(e) => setType(e.target.value as InputFieldType)}>
                  {INPUT_FIELD_TYPES.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
                </select>
                <span className="wf-type-badge">{jsonTypeOf(type)}</span>
              </div>
            </div>

            <div className="wf-form-row">
              <span className="wf-form-label">Variable name<span className="wf-form-required">*</span></span>
              <input
                className="wf-form-input"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="e.g. start_time"
                autoFocus
              />
              {nameError && trimmedName !== "" && <span className="wf-form-help wf-form-help-err">{nameError}</span>}
              {trimmedName !== "" && !nameError && <span className="wf-form-help">{`Referenced as {{input.${trimmedName}}}`}</span>}
            </div>

            <div className="wf-form-row">
              <span className="wf-form-label">Display name</span>
              <input
                className="wf-form-input"
                value={displayName}
                onChange={(e) => setDisplayName(e.target.value)}
                placeholder={trimmedName || "Shown on the run form"}
              />
            </div>

            {type === "select" && (
              <div className="wf-form-row">
                <span className="wf-form-label">Options</span>
                <div className="wf-opts">
                  {options.map((opt, i) => (
                    <div key={i} className="wf-opt-row">
                      <input
                        className="wf-form-input"
                        value={opt}
                        onChange={(e) => setOptions(options.map((o, j) => (j === i ? e.target.value : o)))}
                        placeholder={`Option ${i + 1}`}
                      />
                      <button type="button" className="btn btn-ghost wf-opt-x" onClick={() => setOptions(options.filter((_, j) => j !== i))} title="Remove option">
                        <Ic.Trash size={11} />
                      </button>
                    </div>
                  ))}
                  <button type="button" className="btn btn-ghost wf-opt-add" onClick={() => setOptions([...options, ""])}>
                    <Ic.Plus size={12} /> Add option
                  </button>
                </div>
                {optionsError && <span className="wf-form-help wf-form-help-err">{optionsError}</span>}
              </div>
            )}

            {(type === "text" || type === "paragraph") && (
              <div className="wf-form-row">
                <span className="wf-form-label">Max length</span>
                <input
                  className="wf-form-input"
                  type="number"
                  min={1}
                  value={maxLength}
                  onChange={(e) => setMaxLength(e.target.value)}
                  placeholder="Optional"
                />
              </div>
            )}

            {type === "repository" && (
              <div className="wf-form-row">
                <span className="wf-form-help">The runner picks a project, then a repository; its id is passed as the value.</span>
              </div>
            )}

            {type === "conversation" && (
              <div className="wf-form-row">
                <span className="wf-form-help">The runner picks a conversation (channel / group / DM); its id is passed as the value.</span>
              </div>
            )}

            {!isSelectorFieldType(type) && (
            <div className="wf-form-row">
              <span className="wf-form-label">Default value</span>
              {type === "boolean" ? (
                <select className="wf-form-input" value={defaultBool} onChange={(e) => markDefaultBoolTouched(e.target.value as "" | "true" | "false")}>
                  <option value="">No default</option>
                  <option value="true">Checked</option>
                  <option value="false">Unchecked</option>
                </select>
              ) : type === "select" ? (
                <select
                  className="wf-form-input"
                  value={usableOptions.includes(defaultText) ? defaultText : ""}
                  onChange={(e) => markDefaultTouched(e.target.value)}
                >
                  <option value="">No default</option>
                  {usableOptions.map((o) => <option key={o} value={o}>{o}</option>)}
                </select>
              ) : (
                <input
                  className="wf-form-input"
                  type={type === "number" ? "number" : "text"}
                  value={defaultText}
                  onChange={(e) => markDefaultTouched(e.target.value)}
                  placeholder="Optional — used when the runner leaves it blank"
                />
              )}
            </div>
            )}

            <label className="wf-form-check">
              <input type="checkbox" checked={required} onChange={(e) => setRequired(e.target.checked)} />
              <span>Required</span>
            </label>

            <label className="wf-form-check">
              <input type="checkbox" checked={hidden} onChange={(e) => setHidden(e.target.checked)} />
              <span>Hidden <span className="wf-form-help-inline">— set via default, not shown on the run form</span></span>
            </label>
          </div>
        </div>

        <div className="mdl-foot">
          <button className="btn" onClick={onClose}>Cancel</button>
          <button className="btn btn-primary" disabled={!canSave} onClick={save}>Save</button>
        </div>
      </div>
    </>,
    document.body,
  );
}

function defaultToString(value: unknown): string {
  if (value == null) return "";
  if (typeof value === "string") return value;
  if (typeof value === "number" || typeof value === "boolean") return String(value);
  return JSON.stringify(value);
}

/** Compute the typed default value for the WorkflowVariable from the per-type editor state. */
function resolveDefault(type: InputFieldType, text: string, bool: "" | "true" | "false", options: string[]): unknown {
  if (isSelectorFieldType(type)) return undefined;

  if (type === "boolean") return bool === "" ? undefined : bool === "true";

  if (type === "select") return options.includes(text) ? text : undefined;

  if (type === "number") {
    const t = text.trim();
    if (t === "") return undefined;
    const n = Number(t);
    return Number.isFinite(n) ? n : t;
  }

  // text / paragraph
  return text.trim() === "" ? undefined : text;
}
