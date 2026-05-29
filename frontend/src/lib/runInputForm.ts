import type { WorkflowVariable } from "@/api/workflows";

/** A SchemaForm-ready object schema + seeded values, derived from a workflow's declared inputs. */
export interface RunInputForm {
  schema: { type: "object"; properties: Record<string, unknown>; required: string[] };
  initialValues: Record<string, unknown>;
}

/**
 * Turn a workflow's declared {@link WorkflowVariable} inputs into a single object JSON Schema
 * the existing `SchemaForm` can render — so a manual "Run now" can collect those inputs as the
 * run payload (the Dify-style "fill the form, then run" pattern).
 *
 * Each input becomes one property keyed by its `name`, carrying the input's own per-field JSON
 * Schema. The input's `description` is surfaced as field help when the schema doesn't already
 * carry one; `required` inputs land in the schema's `required` array; and any `default` seeds
 * `initialValues` so the form opens pre-filled. The resulting `{name: value}` object is exactly
 * the shape the engine maps by-name onto `{{input.*}}`.
 *
 * Pure + dependency-free so it's unit-tested without rendering.
 */
export function buildRunInputForm(inputs: readonly WorkflowVariable[]): RunInputForm {
  const properties: Record<string, unknown> = {};
  const required: string[] = [];
  const initialValues: Record<string, unknown> = {};

  for (const input of inputs) {
    const base = (typeof input.schema === "object" && input.schema !== null ? input.schema : {}) as Record<string, unknown>;

    // Surface the declared description as field help, but never clobber a description the
    // field's own schema already provides.
    properties[input.name] = input.description != null && base.description == null
      ? { ...base, description: input.description }
      : base;

    if (input.required) required.push(input.name);

    // Include any supplied default — note `!== undefined` so falsy defaults (0, "", false)
    // still pre-fill the form.
    if (input.default !== undefined) initialValues[input.name] = input.default;
  }

  return { schema: { type: "object", properties, required }, initialValues };
}
