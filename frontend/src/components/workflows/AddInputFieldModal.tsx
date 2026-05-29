import { useEffect, useMemo, useState } from "react";
import { createPortal } from "react-dom";

import { Ic } from "@/_imported/ai-code-space/icons";
import type { WorkflowVariable } from "@/api/workflows";
import {
  buildFieldSchema,
  INPUT_FIELD_TYPES,
  type InputFieldType,
  schemaMaxLength,
  schemaOptions,
  schemaToFieldType,
  isFieldHidden,
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
 * (type / name / display name / max length / options / default / required / hidden) and emits a
 * {@link WorkflowVariable} whose `schema` is built by `buildFieldSchema`. Warm-theme `.mdl` shell.
 */
export function AddInputFieldModal({ initial, takenNames, onSave, onClose }: AddInputFieldModalProps) {
  const [type, setType] = useState<InputFieldType>(initial ? schemaToFieldType(initial.schema) : "text");
  const [name, setName] = useState(initial?.name ?? "");
  const [displayName, setDisplayName] = useState(initial?.label ?? "");
  const [maxLength, setMaxLength] = useState<string>(() => {
    const m = initial ? schemaMaxLength(initial.schema) : null;
    return m != null ? String(m) : "";
  });
  const [optionsText, setOptionsText] = useState(() => (initial ? schemaOptions(initial.schema).join("\n") : ""));
  const [defaultText, setDefaultText] = useState(() => defaultToString(initial?.default));
  const [required, setRequired] = useState(initial?.required ?? true);
  const [hidden, setHidden] = useState(initial ? isFieldHidden(initial.schema) : false);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const trimmedName = name.trim();
  const nameError = useMemo(() => {
    if (trimmedName === "") return "Required";
    if (!NAME_RE.test(trimmedName)) return "Letters, digits, underscore; can't start with a digit";
    if (takenNames.includes(trimmedName)) return "Already used";
    return null;
  }, [trimmedName, takenNames]);

  const canSave = nameError === null;

  const save = () => {
    if (!canSave) return;
    const options = optionsText.split("\n").map((o) => o.trim()).filter((o) => o !== "");
    const maxLen = maxLength.trim() === "" ? null : Number(maxLength);
    const schema = buildFieldSchema({ type, maxLength: Number.isFinite(maxLen) ? maxLen : null, options, hidden });

    onSave({
      name: trimmedName,
      label: displayName.trim() === "" ? null : displayName.trim(),
      schema,
      default: parseDefault(type, defaultText),
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
              <select className="wf-form-input" value={type} onChange={(e) => setType(e.target.value as InputFieldType)}>
                {INPUT_FIELD_TYPES.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
              </select>
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
                <textarea
                  className="wf-form-input wf-form-textarea"
                  value={optionsText}
                  onChange={(e) => setOptionsText(e.target.value)}
                  placeholder="One option per line"
                  rows={4}
                />
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

            <div className="wf-form-row">
              <span className="wf-form-label">Default value</span>
              <input
                className="wf-form-input"
                value={defaultText}
                onChange={(e) => setDefaultText(e.target.value)}
                placeholder="Optional — used when the runner leaves it blank"
              />
            </div>

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

function parseDefault(type: InputFieldType, raw: string): unknown {
  const trimmed = raw.trim();
  if (trimmed === "") return undefined;
  if (type === "number") {
    const n = Number(trimmed);
    return Number.isFinite(n) ? n : trimmed;
  }
  return raw;
}
