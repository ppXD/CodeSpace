import { describe, expect, it } from "vitest";

import { parsePlainForDisplay } from "./parse-plain-for-display";

describe("VariableTablePanel plain value display", () => {
  it("shows a String value without its JSON quotes", () => {
    expect(parsePlainForDisplay("String", "\"hello\"")).toBe("hello");
  });

  it("falls back to the stored text when a String value is not valid JSON", () => {
    expect(parsePlainForDisplay("String", "hello")).toBe("hello");
  });

  it("keeps typed values readable without their JSON wrapper", () => {
    expect(parsePlainForDisplay("Number", "42")).toBe("42");
    expect(parsePlainForDisplay("Boolean", "true")).toBe("true");
  });

  it("leaves Object and Array values as the JSON the operator edits", () => {
    expect(parsePlainForDisplay("Object", "{\"a\":1}")).toBe("{\"a\":1}");
    expect(parsePlainForDisplay("Array", "[1,2]")).toBe("[1,2]");
  });

});
