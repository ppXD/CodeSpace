import { describe, expect, it } from "vitest";

import {
  buildFieldSchema,
  isFieldHidden,
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

  it("hidden → x-hidden:true", () => {
    expect(buildFieldSchema({ type: "text", hidden: true })).toEqual({ type: "string", "x-hidden": true });
  });
});

describe("schemaToFieldType", () => {
  it("round-trips each type", () => {
    expect(schemaToFieldType(buildFieldSchema({ type: "text" }))).toBe("text");
    expect(schemaToFieldType(buildFieldSchema({ type: "paragraph" }))).toBe("paragraph");
    expect(schemaToFieldType(buildFieldSchema({ type: "number" }))).toBe("number");
    expect(schemaToFieldType(buildFieldSchema({ type: "select", options: ["a"] }))).toBe("select");
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
