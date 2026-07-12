/**
 * Infer a JSON-Schema-ish shape from a SAMPLE JSON value. An HTTP / LLM node's response is dynamic — the
 * manifest can't declare its shape — so the author pastes a sample response and this turns it into the nested
 * `{type, properties, items}` shape the {{ref}} picker already knows how to drill (Response → Customer → Email).
 * Pure + total: any input yields some shape; invalid text yields null. Depth-bounded against pathological input.
 */
const MAX_DEPTH = 8;

export function inferSchema(value: unknown, depth = 0): Record<string, unknown> {
  if (depth > MAX_DEPTH || value === null) return {};
  if (Array.isArray(value)) {
    return value.length > 0 ? { type: "array", items: inferSchema(value[0], depth + 1) } : { type: "array" };
  }
  switch (typeof value) {
    case "string": return { type: "string" };
    case "boolean": return { type: "boolean" };
    case "number": return { type: Number.isInteger(value) ? "integer" : "number" };
    case "object": {
      const properties: Record<string, unknown> = {};
      for (const [k, v] of Object.entries(value as Record<string, unknown>)) properties[k] = inferSchema(v, depth + 1);
      return { type: "object", properties };
    }
    default: return {};
  }
}

/** Parse a pasted sample and infer its shape; null when the text isn't valid JSON (so the caller can show a hint). */
export function inferSchemaFromSample(text: string | undefined): Record<string, unknown> | null {
  const t = (text ?? "").trim();
  if (!t) return null;
  try {
    return inferSchema(JSON.parse(t));
  } catch {
    return null;
  }
}

/** How many leaf fields the inferred shape exposes — for the editor's "N fields now drillable" confirmation. */
export function countLeafFields(schema: Record<string, unknown> | null): number {
  if (!schema) return 0;
  const props = schema.properties as Record<string, unknown> | undefined;
  const items = schema.items as Record<string, unknown> | undefined;
  if (props && typeof props === "object") {
    return Object.values(props).reduce<number>((n, child) => n + Math.max(1, countLeafFields(child as Record<string, unknown>)), 0);
  }
  if (schema.type === "array" && items && typeof items === "object" && items.properties) {
    return countLeafFields(items);
  }
  return schema.type ? 1 : 0;
}
