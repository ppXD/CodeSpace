import { describe, expect, it } from "vitest";

import type { WorkflowVariable } from "@/api/workflows";

import { buildRunInputForm } from "./runInputForm";

const input = (over: Partial<WorkflowVariable> & { name: string }): WorkflowVariable => ({
  schema: { type: "string" },
  ...over,
});

describe("buildRunInputForm", () => {
  it("returns an empty object schema for no inputs", () => {
    const { schema, initialValues } = buildRunInputForm([]);
    expect(schema).toEqual({ type: "object", properties: {}, required: [] });
    expect(initialValues).toEqual({});
  });

  it("maps each input to a property keyed by name, carrying its own schema", () => {
    const { schema } = buildRunInputForm([
      input({ name: "customer_email", schema: { type: "string", format: "email" } }),
      input({ name: "count", schema: { type: "integer" } }),
    ]);
    expect(Object.keys(schema.properties)).toEqual(["customer_email", "count"]);
    expect(schema.properties.customer_email).toEqual({ type: "string", format: "email" });
    expect(schema.properties.count).toEqual({ type: "integer" });
  });

  it("collects required input names into the schema's required list", () => {
    const { schema } = buildRunInputForm([
      input({ name: "a", required: true }),
      input({ name: "b", required: false }),
      input({ name: "c", required: true }),
    ]);
    expect(schema.required).toEqual(["a", "c"]);
  });

  it("seeds initialValues from defaults, including falsy ones", () => {
    const { initialValues } = buildRunInputForm([
      input({ name: "greeting", default: "hi" }),
      input({ name: "limit", schema: { type: "integer" }, default: 0 }),
      input({ name: "enabled", schema: { type: "boolean" }, default: false }),
      input({ name: "no_default" }),
    ]);
    expect(initialValues).toEqual({ greeting: "hi", limit: 0, enabled: false });
    expect("no_default" in initialValues).toBe(false);
  });

  it("surfaces the input description as field help when the schema has none", () => {
    const { schema } = buildRunInputForm([
      input({ name: "email", description: "Where to send the summary" }),
    ]);
    expect((schema.properties.email as { description?: string }).description).toBe("Where to send the summary");
  });

  it("does not clobber a description the field schema already declares", () => {
    const { schema } = buildRunInputForm([
      input({ name: "email", schema: { type: "string", description: "schema-level" }, description: "input-level" }),
    ]);
    expect((schema.properties.email as { description?: string }).description).toBe("schema-level");
  });
});
