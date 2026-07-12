import { describe, expect, it } from "vitest";

import { countLeafFields, inferSchema, inferSchemaFromSample } from "./jsonShape";

describe("inferSchema", () => {
  it("infers nested object + scalar types from a sample", () => {
    const schema = inferSchema({ customer: { profile: { email: "a@b.com", age: 30, vip: true } } });
    expect(schema).toEqual({
      type: "object",
      properties: {
        customer: {
          type: "object",
          properties: {
            profile: {
              type: "object",
              properties: {
                email: { type: "string" },
                age: { type: "integer" },
                vip: { type: "boolean" },
              },
            },
          },
        },
      },
    });
  });

  it("infers an array of objects from the first element (drillable as items)", () => {
    expect(inferSchema({ orders: [{ id: 1, total: 9.5 }] })).toEqual({
      type: "object",
      properties: {
        orders: { type: "array", items: { type: "object", properties: { id: { type: "integer" }, total: { type: "number" } } } },
      },
    });
  });

  it("leaves an empty array / null as an unshaped leaf (nothing to drill)", () => {
    expect(inferSchema({ tags: [], note: null })).toEqual({
      type: "object",
      properties: { tags: { type: "array" }, note: {} },
    });
  });
});

describe("inferSchemaFromSample", () => {
  it("parses valid JSON text into a shape", () => {
    expect(inferSchemaFromSample('{"x": "y"}')).toEqual({ type: "object", properties: { x: { type: "string" } } });
  });
  it("returns null for invalid / empty text", () => {
    expect(inferSchemaFromSample("not json")).toBeNull();
    expect(inferSchemaFromSample("   ")).toBeNull();
    expect(inferSchemaFromSample(undefined)).toBeNull();
  });
});

describe("countLeafFields", () => {
  it("counts the drillable leaves", () => {
    expect(countLeafFields(inferSchema({ a: 1, b: { c: "x", d: "y" } }))).toBe(3); // a, b.c, b.d
    expect(countLeafFields(null)).toBe(0);
  });
});
