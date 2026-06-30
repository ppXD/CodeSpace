import { describe, expect, it } from "vitest";

import type { AgentBoundSkill } from "@/api/agents";
import type { SkillSummary } from "@/api/skills";

import { skillLabels } from "./skillPicker";

const skill = (id: string, slug: string, name: string): SkillSummary => ({
  id, teamId: "t", slug, name, description: null, category: null, origin: "Authored", packId: null, sourceDefinitionId: null, createdDate: "2026-01-01T00:00:00Z",
});
const bound = (id: string, slug: string, name: string): AgentBoundSkill => ({ skillDefinitionId: id, slug, name });

describe("skillLabels", () => {
  it("labels by the display NAME (not the disambiguated slug), live list winning over the bound fallback", () => {
    // "a" has slug tdd-2 (a disambiguated handle) but name "TDD" — the label must be the name, never the slug.
    const labels = skillLabels([skill("a", "tdd-2", "TDD")], [bound("a", "tdd-2", "stale"), bound("b", "debugging-3", "Debugging")]);
    expect(labels.get("a")).toBe("TDD");        // live list wins, by name (not "tdd-2")
    expect(labels.get("b")).toBe("Debugging");  // bound-only fallback, by name (not "debugging-3")
  });

  it("is empty with no inputs", () => {
    expect(skillLabels([], []).size).toBe(0);
  });
});
