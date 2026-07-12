import { describe, expect, it } from "vitest";

import { allowedOperators, parseCondition, quoteRight, serializeCondition, type IfCondition } from "./ifCondition";

const roundtrip = (expr: string) => serializeCondition(parseCondition(expr));

describe("parse/serialize condition round-trip (matches the engine grammar)", () => {
  it.each([
    '{{nodes.fetch.outputs.allPassed}}',                         // bare truthiness
    '{{trigger.number}} > 100',
    '{{trigger.number}} >= 100',                                  // >= wins over >
    '{{trigger.state}} == "open"',                                // quoted string literal
    '{{trigger.author}} != "alice"',
    '{{nodes.fetch.outputs.files}} is_not_empty',                // unary
    '{{nodes.fetch.outputs.files}} is_empty',
    '{{trigger.title}} contains "urgent"',
    '{{trigger.branch}} startsWith "feat/"',
    '{{a}} == {{b}}',                                             // ref on both sides
    '{{n}} == 42',                                                // number literal
    '{{flag}} == true',                                          // bool literal
  ])("round-trips %s unchanged", (expr) => {
    expect(roundtrip(expr)).toBe(expr);
  });

  it("parses the parts of a binary condition", () => {
    expect(parseCondition('{{trigger.state}} == "open"')).toEqual({ left: "{{trigger.state}}", op: "==", right: "open" });
  });

  it("shows a quoted string literal unquoted in the editor, re-quoting on save", () => {
    const parsed = parseCondition('{{x}} contains "hello world"');
    expect(parsed.right).toBe("hello world");                    // editor holds plain text
    expect(serializeCondition(parsed)).toBe('{{x}} contains "hello world"');
  });

  it("treats a bare value as truthiness", () => {
    expect(parseCondition("{{flag}}")).toEqual({ left: "{{flag}}", op: "truthy", right: "" });
    expect(serializeCondition({ left: "{{flag}}", op: "truthy", right: "" })).toBe("{{flag}}");
  });

  it("empty condition ⇄ empty structured form", () => {
    expect(parseCondition("")).toEqual({ left: "", op: "truthy", right: "" });
    expect(serializeCondition({ left: "", op: "==", right: "x" })).toBe("");
  });

  it("drops the operator when a binary op has no right value yet (stays a valid partial)", () => {
    expect(serializeCondition({ left: "{{x}}", op: "==", right: "" })).toBe("{{x}}");
  });
});

describe("quoteRight", () => {
  it("passes refs / numbers / bools through, quotes bare strings", () => {
    expect(quoteRight("{{nodes.x.outputs.y}}")).toBe("{{nodes.x.outputs.y}}");
    expect(quoteRight("42")).toBe("42");
    expect(quoteRight("true")).toBe("true");
    expect(quoteRight("open")).toBe('"open"');
    expect(quoteRight('"already"')).toBe('"already"');
    expect(quoteRight("")).toBe("");
  });
});

describe("allowedOperators — type-aware", () => {
  const labels = (c?: IfCondition, type?: string) => allowedOperators(type ?? c?.op).map((o) => o.value);

  it("a boolean value only offers is-true / equals / not-equals", () => {
    expect(labels(undefined, "boolean")).toEqual(["==", "!=", "truthy"]);
  });

  it("a number offers comparisons, not string ops", () => {
    const ops = allowedOperators("integer").map((o) => o.value);
    expect(ops).toContain(">");
    expect(ops).toContain("<=");
    expect(ops).not.toContain("startsWith");
  });

  it("a string offers contains / startsWith, not numeric comparisons", () => {
    const ops = allowedOperators("string").map((o) => o.value);
    expect(ops).toContain("contains");
    expect(ops).not.toContain(">");
  });

  it("a list (array) only offers emptiness checks", () => {
    expect(allowedOperators("array").map((o) => o.value)).toEqual(["is_not_empty", "is_empty"]);
  });

  it("an unknown type offers every operator", () => {
    expect(allowedOperators(undefined).length).toBe(12);
  });
});
