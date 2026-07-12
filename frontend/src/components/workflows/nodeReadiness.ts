/**
 * Pure readiness check for the inspector's status line. A node is "ready" when every REQUIRED field the
 * manifest declares (across its Config and Inputs schemas) currently holds a value. This is advisory — the
 * engine doesn't hard-enforce `required` at runtime — but it's the one place the author sees, before running,
 * that a node still needs attention. No React, no side effects: takes schemas + current values, returns a verdict.
 *
 * Mirrors SchemaForm's two conventions so the summary agrees with the form the author is looking at:
 *   - `title` overrides the humanized property name (same label the field shows).
 *   - `x-showWhen` hides a field until a sibling equals a value — a hidden required field is NOT counted
 *     as missing (the engine ignores it, and the form doesn't render it).
 * A `{{ref}}` template counts as present: it's a non-empty string that resolves at run time.
 */

interface ReadinessProp {
  title?: string;
  "x-showWhen"?: { field: string; equals: unknown };
}

interface ReadinessSchema {
  properties?: Record<string, ReadinessProp>;
  required?: string[];
}

export interface RequiredField {
  key: string;
  label: string;
}

export interface NodeReadiness {
  /** Visible required fields across Config + Inputs. Zero ⇒ the node has nothing to fill (hide the status line). */
  requiredCount: number;
  /** The subset of required fields still empty. Empty ⇒ ready to run. */
  missing: RequiredField[];
}

function asSchema(raw: unknown): ReadinessSchema {
  return typeof raw === "object" && raw != null ? (raw as ReadinessSchema) : {};
}

function asObject(raw: unknown): Record<string, unknown> {
  return typeof raw === "object" && raw != null ? (raw as Record<string, unknown>) : {};
}

function humanize(name: string): string {
  // camelCase → "Camel Case", snake_case → "Snake Case" — identical to SchemaForm's label rule.
  return name
    .replace(/([a-z])([A-Z])/g, "$1 $2")
    .replace(/_/g, " ")
    .replace(/^\w/, (c) => c.toUpperCase());
}

function isEmpty(value: unknown): boolean {
  return (
    value === undefined ||
    value === null ||
    value === "" ||
    (Array.isArray(value) && value.length === 0)
  );
}

/** Required fields that are currently VISIBLE (x-showWhen met), each tagged with whether its value is empty. */
function visibleRequired(schema: unknown, value: unknown): { field: RequiredField; empty: boolean }[] {
  const s = asSchema(schema);
  const obj = asObject(value);
  const props = s.properties ?? {};

  return (s.required ?? [])
    .filter((key) => {
      const cond = props[key]?.["x-showWhen"];
      return !cond || obj[cond.field] === cond.equals;
    })
    .map((key) => ({
      field: { key, label: props[key]?.title ?? humanize(key) },
      empty: isEmpty(obj[key]),
    }));
}

export function nodeReadiness(
  configSchema: unknown,
  config: unknown,
  inputSchema: unknown,
  inputs: unknown,
): NodeReadiness {
  const fields = [...visibleRequired(configSchema, config), ...visibleRequired(inputSchema, inputs)];

  return {
    requiredCount: fields.length,
    missing: fields.filter((f) => f.empty).map((f) => f.field),
  };
}
