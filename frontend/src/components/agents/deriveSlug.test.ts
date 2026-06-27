import { describe, expect, it } from "vitest";

import { deriveSlug } from "./deriveSlug";

describe("deriveSlug", () => {
  it("lowercases and hyphenates a normal name", () => {
    expect(deriveSlug("Backend Architect")).toBe("backend-architect");
  });

  it("collapses runs of non-alphanumerics to a single hyphen and trims edges", () => {
    expect(deriveSlug("  Code   Reviewer!! ")).toBe("code-reviewer");
    expect(deriveSlug("--weird--name--")).toBe("weird-name");
  });

  it("keeps digits and underscores; drops everything else", () => {
    expect(deriveSlug("Agent_42 (v2)")).toBe("agent_42-v2");
  });

  it("returns empty when no usable character survives (caller warns before save)", () => {
    expect(deriveSlug("")).toBe("");
    expect(deriveSlug("   ")).toBe("");
    expect(deriveSlug("!!!")).toBe("");
  });

  it("caps at 64 characters and trims a trailing hyphen left by the cut", () => {
    const long = "a".repeat(70);
    expect(deriveSlug(long)).toHaveLength(64);

    // A name that would place a hyphen at position 64 must not end with one.
    const cut = "x".repeat(64) + " tail";
    expect(deriveSlug(cut).endsWith("-")).toBe(false);
  });
});
