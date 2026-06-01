/**
 * Maps the Dify-style "Add variable" field types to the JSON Schema fragment stored on a
 * workflow input (`WorkflowVariable.schema`), and back. Keeps the editor's friendly type
 * picker decoupled from the on-disk schema the engine + SchemaForm consume.
 *
 * The schema is the source of truth (backend-opaque `unknown`); the editor-only concerns —
 * "this is a long-text field" and "hide this from the run form" — ride along as `x-long` /
 * `x-hidden` extension keys the engine ignores.
 */

export type InputFieldType = "text" | "paragraph" | "number" | "select" | "boolean" | "repository" | "conversation";

/** Field types backed by an entity picker — their value is an id string and their x-selector key equals the type name. */
const SELECTOR_FIELD_TYPES = ["repository", "conversation"] as const;

/** JSON Schema primitive each editor type compiles down to (shown as a badge in the picker). */
export type JsonType = "string" | "number" | "boolean";

export const INPUT_FIELD_TYPES: ReadonlyArray<{ value: InputFieldType; label: string; jsonType: JsonType }> = [
  { value: "text", label: "Text", jsonType: "string" },
  { value: "paragraph", label: "Paragraph", jsonType: "string" },
  { value: "select", label: "Select", jsonType: "string" },
  { value: "number", label: "Number", jsonType: "number" },
  { value: "boolean", label: "Checkbox", jsonType: "boolean" },
  // Entity-picker fields: an id chosen at run time via a picker, stored as a plain string (the
  // UUID), so the engine sees an ordinary `{{input.<name>}}` string. Repository = project→repo
  // picker; Conversation = the team's conversations (channel / group / DM).
  { value: "repository", label: "Repository", jsonType: "string" },
  { value: "conversation", label: "Conversation", jsonType: "string" },
];

/** The JSON primitive a field type compiles to — drives both the schema and the picker badge. */
export function jsonTypeOf(type: InputFieldType): JsonType {
  return INPUT_FIELD_TYPES.find((t) => t.value === type)?.jsonType ?? "string";
}

/** True for entity-picker field types (repository, conversation): rendered via a selector, no hand-typed default. */
export function isSelectorFieldType(type: InputFieldType): boolean {
  return (SELECTOR_FIELD_TYPES as readonly string[]).includes(type);
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

  // An entity-picker field is a string id rendered by its selector (x-selector dispatch); the
  // selector key is the type name (repository → project→repo picker, conversation → conversation picker).
  if ((SELECTOR_FIELD_TYPES as readonly string[]).includes(draft.type)) schema["x-selector"] = draft.type;

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
  const selector = SELECTOR_FIELD_TYPES.find((t) => t === s["x-selector"]);
  if (selector) return selector;
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

/**
 * Coerce a number/integer form field's raw editor text into the value to persist. A plain
 * numeric literal becomes a JSON number (so a node expecting a number gets one); a value
 * containing a `{{ref}}` template stays a string (a lone `{{ref}}` is type-preserved by the
 * engine, so it still resolves to a number at run time); blank becomes undefined. Anything
 * else (a non-numeric literal) is kept verbatim so the operator can see + fix their typo.
 */
export function coerceNumberInput(raw: string): number | string | undefined {
  const trimmed = raw.trim();
  if (trimmed === "") return undefined;
  if (raw.includes("{{")) return raw;
  if (/^-?\d+(\.\d+)?$/.test(trimmed)) return Number(trimmed);
  return raw;
}
