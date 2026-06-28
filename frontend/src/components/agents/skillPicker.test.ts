import { describe, expect, it } from "vitest";

import type { AgentBoundSkill } from "@/api/agents";
import type { SkillSummary } from "@/api/skills";

import { skillLabels } from "./skillPicker";

const skill = (id: string, slug: string, name: string): SkillSummary => ({
  id, teamId: "t", slug, name, description: null, category: null, origin: "Authored", packId: null, sourceDefinitionId: null, createdDate: "2026-01-01T00:00:00Z",
});
const bound = (id: string, slug: string): AgentBoundSkill => ({ skillDefinitionId: id, slug, name: slug });

describe("skillLabels", () => {
  it("labels by slug, live list winning over the bound fallback", () => {
    const labels = skillLabels([skill("a", "tdd", "TDD")], [bound("a", "stale"), bound("b", "debugging")]);
    expect(labels.get("a")).toBe("tdd");        // live list wins
    expect(labels.get("b")).toBe("debugging");  // bound-only fallback (not yet/no longer in the list)
  });

  it("is empty with no inputs", () => {
    expect(skillLabels([], []).size).toBe(0);
  });
});
