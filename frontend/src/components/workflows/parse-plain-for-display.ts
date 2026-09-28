import type { VariableValueType } from "@/api/variables";

/**
 * Decode the stored JSON-encoded plaintext for display. String values are stored as JSON
 * text (including quotes), so showing the raw column would render `"value"` in the editor.
 * Object / Array keep their JSON text verbatim because that is what the operator edits.
 */
export function parsePlainForDisplay(valueType: VariableValueType, valuePlain: string | null): string {
  const raw = valuePlain ?? "";
  if (valueType === "Object" || valueType === "Array" || raw === "") return raw;

  try {
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed === "string") return parsed;
    if (typeof parsed === "number" || typeof parsed === "boolean") return String(parsed);
  } catch {
    // Malformed stored JSON falls back to the raw text so the operator can see and repair it.
  }

  return raw;
}
