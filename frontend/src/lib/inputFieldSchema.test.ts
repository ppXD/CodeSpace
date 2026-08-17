import { describe, expect, it } from "vitest";

import {
  buildFieldSchema,
  coerceNumberInput,
  isFieldHidden,
  isSelectorFieldType,
  jsonTypeOf,
  schemaMaxLength,
  schemaOptions,
  schemaToFieldType,
} from "./inputFieldSchema";

describe("buildFieldSchema", () => {
  it("text → string, with optional maxLength", () => {
    expect(buildFieldSchema({ type: "text" })).toEqual({ type: "string" });
    expect(buildFieldSchema({ type: "text", maxLength: 48 })).toEqual({ type: "string", maxLength: 48 });
  });

  it("paragraph → string + x-long marker", () => {
    expect(buildFieldSchema({ type: "paragraph" })).toEqual({ type: "string", "x-long": true });
    expect(buildFieldSchema({ type: "paragraph", maxLength: 500 })).toEqual({ type: "string", "x-long": true, maxLength: 500 });
  });

  it("number → number (maxLength ignored)", () => {
    expect(buildFieldSchema({ type: "number", maxLength: 10 })).toEqual({ type: "number" });
  });

  it("select → string + enum of non-empty trimmed options", () => {
    expect(buildFieldSchema({ type: "select", options: [" a ", "", "b", "  "] })).toEqual({ type: "string", enum: ["a", "b"] });
  });

  it("select with no usable options → plain string (no empty enum)", () => {
    expect(buildFieldSchema({ type: "select", options: ["", "  "] })).toEqual({ type: "string" });
  });

  it("ignores zero/negative maxLength", () => {
    expect(buildFieldSchema({ type: "text", maxLength: 0 })).toEqual({ type: "string" });
  });

  it("checkbox → boolean", () => {
    expect(buildFieldSchema({ type: "boolean" })).toEqual({ type: "boolean" });
    expect(buildFieldSchema({ type: "boolean", hidden: true })).toEqual({ type: "boolean", "x-hidden": true });
  });

  it("repository → string with x-selector (renders the project→repo picker)", () => {
    expect(buildFieldSchema({ type: "repository" })).toEqual({ type: "string", "x-selector": "repository" });
    expect(buildFieldSchema({ type: "repository", hidden: true })).toEqual({ type: "string", "x-selector": "repository", "x-hidden": true });
  });

  it("conversation → string with x-selector (renders the conversation picker)", () => {
    expect(buildFieldSchema({ type: "conversation" })).toEqual({ type: "string", "x-selector": "conversation" });
  });

  it("hidden → x-hidden:true", () => {
    expect(buildFieldSchema({ type: "text", hidden: true })).toEqual({ type: "string", "x-hidden": true });
  });
});

describe("jsonTypeOf", () => {
  it("maps each editor type to its JSON primitive", () => {
    expect(jsonTypeOf("text")).toBe("string");
    expect(jsonTypeOf("paragraph")).toBe("string");
    expect(jsonTypeOf("select")).toBe("string");
    expect(jsonTypeOf("number")).toBe("number");
    expect(jsonTypeOf("boolean")).toBe("boolean");
    expect(jsonTypeOf("repository")).toBe("string");
    expect(jsonTypeOf("conversation")).toBe("string");
  });
});

describe("isSelectorFieldType", () => {
  it("is true only for entity-picker field types", () => {
    expect(isSelectorFieldType("repository")).toBe(true);
    expect(isSelectorFieldType("conversation")).toBe(true);
    expect(isSelectorFieldType("text")).toBe(false);
    expect(isSelectorFieldType("select")).toBe(false);
  });
});

describe("schemaToFieldType", () => {
  it("round-trips each type", () => {
    expect(schemaToFieldType(buildFieldSchema({ type: "text" }))).toBe("text");
    expect(schemaToFieldType(buildFieldSchema({ type: "paragraph" }))).toBe("paragraph");
    expect(schemaToFieldType(buildFieldSchema({ type: "number" }))).toBe("number");
    expect(schemaToFieldType(buildFieldSchema({ type: "select", options: ["a"] }))).toBe("select");
    expect(schemaToFieldType(buildFieldSchema({ type: "boolean" }))).toBe("boolean");
    expect(schemaToFieldType(buildFieldSchema({ type: "repository" }))).toBe("repository");
    expect(schemaToFieldType(buildFieldSchema({ type: "conversation" }))).toBe("conversation");
  });

  it("treats integer as number and unknown as text", () => {
    expect(schemaToFieldType({ type: "integer" })).toBe("number");
    expect(schemaToFieldType({})).toBe("text");
    expect(schemaToFieldType(null)).toBe("text");
  });
});

describe("schema readers", () => {
  it("schemaMaxLength reads number or null", () => {
    expect(schemaMaxLength({ type: "string", maxLength: 12 })).toBe(12);
    expect(schemaMaxLength({ type: "string" })).toBeNull();
  });

  it("schemaOptions reads enum as strings", () => {
    expect(schemaOptions({ enum: ["a", "b"] })).toEqual(["a", "b"]);
    expect(schemaOptions({})).toEqual([]);
  });

  it("isFieldHidden reads x-hidden", () => {
    expect(isFieldHidden({ "x-hidden": true })).toBe(true);
    expect(isFieldHidden({})).toBe(false);
  });
});

describe("coerceNumberInput", () => {
  it("turns a numeric literal into a JSON number", () => {
    expect(coerceNumberInput("123")).toBe(123);
    expect(coerceNumberInput("-4")).toBe(-4);
    expect(coerceNumberInput("3.5")).toBe(3.5);
    expect(coerceNumberInput("  42  ")).toBe(42);
  });

  it("keeps a {{ref}} template as a string (engine type-preserves a lone ref)", () => {
    expect(coerceNumberInput("{{input.pr_number}}")).toBe("{{input.pr_number}}");
  });

  it("blank → undefined", () => {
    expect(coerceNumberInput("")).toBeUndefined();
    expect(coerceNumberInput("   ")).toBeUndefined();
  });

  it("keeps a non-numeric literal verbatim (operator sees + fixes the typo)", () => {
    expect(coerceNumberInput("abc")).toBe("abc");
  });

  it("carries through a keyword the editor has no control for", () => {
    // A planner (or a hand-edited definition) can put anything in a field's schema. Rebuilding from
    // {} deleted all of it; the editor may only clear the keywords it owns.
    const stored = { type: "string", description: "Set by the planner", format: "uri", "x-spotlight": 3 };

    const patched = buildFieldSchema({ type: "text", maxLength: null, options: [], hidden: false }, stored);

    expect(patched.description).toBe("Set by the planner");
    expect(patched.format).toBe("uri");
    expect(patched["x-spotlight"]).toBe(3);
  });

  it("still clears a keyword it does own", () => {
    // The other half: unticking Hidden has to actually remove x-hidden, or the merge would make the
    // control one-way.
    const stored = { type: "string", "x-hidden": true, maxLength: 40, description: "kept" };

    const patched = buildFieldSchema({ type: "text", maxLength: null, options: [], hidden: false }, stored);

    expect(patched["x-hidden"]).toBeUndefined();
    expect(patched.maxLength).toBeUndefined();
    expect(patched.description).toBe("kept");
  });

  it("builds from scratch when there is nothing stored", () => {
    expect(buildFieldSchema({ type: "text", maxLength: null, options: [], hidden: false })).toEqual({ type: "string" });
  });
});
