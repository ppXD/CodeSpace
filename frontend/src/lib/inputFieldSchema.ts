/**
 * Maps the Dify-style "Add variable" field types to the JSON Schema fragment stored on a
 * workflow input (`WorkflowVariable.schema`), and back. Keeps the editor's friendly type
 * picker decoupled from the on-disk schema the engine + SchemaForm consume.
 *
 * The schema is the source of truth (backend-opaque `unknown`); the editor-only concerns —
 * "this is a long-text field" and "hide this from the run form" — ride along as `x-long` /
 * `x-hidden` extension keys the engine ignores.
 */

export type InputFieldType = "text" | "paragraph" | "number" | "select" | "boolean";

/** JSON Schema primitive each editor type compiles down to (shown as a badge in the picker). */
export type JsonType = "string" | "number" | "boolean";

export const INPUT_FIELD_TYPES: ReadonlyArray<{ value: InputFieldType; label: string; jsonType: JsonType }> = [
  { value: "text", label: "Text", jsonType: "string" },
  { value: "paragraph", label: "Paragraph", jsonType: "string" },
  { value: "select", label: "Select", jsonType: "string" },
  { value: "number", label: "Number", jsonType: "number" },
  { value: "boolean", label: "Checkbox", jsonType: "boolean" },
];

/** The JSON primitive a field type compiles to — drives both the schema and the picker badge. */
export function jsonTypeOf(type: InputFieldType): JsonType {
  return INPUT_FIELD_TYPES.find((t) => t.value === type)?.jsonType ?? "string";
}

export interface InputFieldDraft {
  type: InputFieldType;
  maxLength?: number | null;
  /** Select options (non-empty entries only). Ignored for non-select types. */
  options?: string[];
  hidden?: boolean;
}

/** Build the per-field JSON Schema for an input field from the editor draft. */
export function buildFieldSchema(draft: InputFieldDraft): Record<string, unknown> {
  const schema: Record<string, unknown> = { type: jsonTypeOf(draft.type) };

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
  if (s.type === "boolean") return "boolean";
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
