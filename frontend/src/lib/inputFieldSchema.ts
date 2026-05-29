/**
 * Maps the Dify-style "Add variable" field types to the JSON Schema fragment stored on a
 * workflow input (`WorkflowVariable.schema`), and back. Keeps the editor's friendly type
 * picker decoupled from the on-disk schema the engine + SchemaForm consume.
 *
 * The schema is the source of truth (backend-opaque `unknown`); the editor-only concerns —
 * "this is a long-text field" and "hide this from the run form" — ride along as `x-long` /
 * `x-hidden` extension keys the engine ignores.
 */

export type InputFieldType = "text" | "paragraph" | "number" | "select";

export const INPUT_FIELD_TYPES: ReadonlyArray<{ value: InputFieldType; label: string }> = [
  { value: "text", label: "Text" },
  { value: "paragraph", label: "Paragraph" },
  { value: "number", label: "Number" },
  { value: "select", label: "Select" },
];

export interface InputFieldDraft {
  type: InputFieldType;
  maxLength?: number | null;
  /** Select options (non-empty entries only). Ignored for non-select types. */
  options?: string[];
  hidden?: boolean;
}

/** Build the per-field JSON Schema for an input field from the editor draft. */
export function buildFieldSchema(draft: InputFieldDraft): Record<string, unknown> {
  const schema: Record<string, unknown> = { type: draft.type === "number" ? "number" : "string" };

  if (draft.type === "paragraph") schema["x-long"] = true;

  if (draft.type === "select") {
    const opts = (draft.options ?? []).map((o) => o.trim()).filter((o) => o !== "");
    if (opts.length > 0) schema.enum = opts;
  }

  if ((draft.type === "text" || draft.type === "paragraph") && draft.maxLength != null && draft.maxLength > 0) {
    schema.maxLength = draft.maxLength;
  }

  if (draft.hidden) schema["x-hidden"] = true;

  return schema;
}

/** Infer the editor field type from a stored schema (for editing an existing input). */
export function schemaToFieldType(schema: unknown): InputFieldType {
  const s = asObject(schema);
  if (Array.isArray(s.enum)) return "select";
  if (s.type === "number" || s.type === "integer") return "number";
  if (s["x-long"] === true) return "paragraph";
  return "text";
}

export function schemaMaxLength(schema: unknown): number | null {
  const m = asObject(schema).maxLength;
  return typeof m === "number" ? m : null;
}

export function schemaOptions(schema: unknown): string[] {
  const e = asObject(schema).enum;
  return Array.isArray(e) ? e.map((v) => String(v)) : [];
}

/** A field flagged `x-hidden` is set programmatically (via its default) and never prompted in the run form. */
export function isFieldHidden(schema: unknown): boolean {
  return asObject(schema)["x-hidden"] === true;
}

function asObject(value: unknown): Record<string, unknown> {
  return typeof value === "object" && value !== null ? (value as Record<string, unknown>) : {};
}
