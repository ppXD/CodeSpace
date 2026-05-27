import { describe, expect, it } from "vitest";

import { cn } from "./utils";

/**
 * Smoke test for the shadcn class-merger. Doubles as the canary that the vitest
 * environment + path-alias + TypeScript resolution all line up — if cn ever
 * starts returning wrong results, every shadcn component renders with the
 * wrong tailwind class set, so this is high-leverage even though the function
 * is one line.
 */
describe("cn", () => {
  it("joins truthy class strings", () => {
    expect(cn("a", "b", "c")).toBe("a b c");
  });

  it("drops falsy entries", () => {
    expect(cn("a", false, null, undefined, "", "b")).toBe("a b");
  });

  it("flattens nested arrays / conditional objects", () => {
    expect(cn(["a", { b: true, c: false }, "d"])).toBe("a b d");
  });

  it("resolves Tailwind conflicts via tailwind-merge (later wins)", () => {
    // The whole point of cn over plain clsx — `px-4` should win over `px-2`.
    expect(cn("px-2 py-1", "px-4")).toBe("py-1 px-4");
  });
});
