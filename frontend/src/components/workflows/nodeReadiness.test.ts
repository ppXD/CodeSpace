import { describe, expect, it } from "vitest";

import { nodeReadiness } from "./nodeReadiness";

const configSchema = {
  properties: {
    url: { title: "Endpoint URL" },
    method: {},
    body: { "x-showWhen": { field: "method", equals: "POST" } },
  },
  required: ["url", "method", "body"],
};

describe("nodeReadiness", () => {
  it("reports every required field missing when the value bag is empty (hidden conditionals excluded)", () => {
    const r = nodeReadiness(configSchema, {}, {}, {});

    // `body` is required but hidden (method !== POST) → not counted; url + method are.
    expect(r.requiredCount).toBe(2);
    expect(r.missing.map((f) => f.label)).toEqual(["Endpoint URL", "Method"]);
  });

  it("is ready once every VISIBLE required field has a value", () => {
    const r = nodeReadiness(configSchema, { url: "https://x", method: "GET" }, {}, {});

    expect(r.requiredCount).toBe(2);
    expect(r.missing).toEqual([]);
  });

  it("counts a conditional field once its condition is met, and flags it while empty", () => {
    const r = nodeReadiness(configSchema, { url: "https://x", method: "POST" }, {}, {});

    // method === POST reveals `body` → now 3 required, `body` still empty.
    expect(r.requiredCount).toBe(3);
    expect(r.missing.map((f) => f.key)).toEqual(["body"]);
  });

  it("treats a {{ref}} template as present (non-empty string)", () => {
    const r = nodeReadiness(configSchema, { url: "{{trigger.url}}", method: "GET" }, {}, {});

    expect(r.missing).toEqual([]);
  });

  it("treats null, empty string, and empty array as missing", () => {
    const schema = { properties: { a: {}, b: {}, c: {} }, required: ["a", "b", "c"] };
    const r = nodeReadiness(schema, { a: null, b: "", c: [] }, {}, {});

    expect(r.missing.map((f) => f.key)).toEqual(["a", "b", "c"]);
  });

  it("merges required fields from BOTH the config and input schemas", () => {
    const inputSchema = { properties: { items: {} }, required: ["items"] };
    const r = nodeReadiness(configSchema, { url: "u", method: "GET" }, inputSchema, {});

    expect(r.requiredCount).toBe(3); // url + method (config) + items (inputs)
    expect(r.missing.map((f) => f.key)).toEqual(["items"]);
  });

  it("has no required fields (renders nothing) for a schema with none", () => {
    const r = nodeReadiness({ properties: { x: {} } }, {}, undefined, undefined);

    expect(r.requiredCount).toBe(0);
    expect(r.missing).toEqual([]);
  });
});
