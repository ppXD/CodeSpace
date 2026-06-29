import { describe, expect, it } from "vitest";

import type { AgentBoundSkill } from "@/api/agents";
import type { SkillSummary } from "@/api/skills";

import { availableSkillOptions, skillLabels } from "./skillPicker";

const skill = (id: string, slug: string, name: string): SkillSummary => ({
  id, teamId: "t", slug, name, description: null, category: null, origin: "Authored", packId: null, createdDate: "2026-01-01T00:00:00Z",
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

describe("availableSkillOptions", () => {
  it("offers only the unselected skills, mapped to name + @slug", () => {
    const skills = [skill("a", "tdd", "TDD"), skill("b", "debugging", "Debugging"), skill("c", "repro", "Repro")];
    const opts = availableSkillOptions(skills, ["b"]);
    expect(opts.map((o) => o.value)).toEqual(["a", "c"]);
    expect(opts[0]).toEqual({ value: "a", label: "TDD", desc: "@tdd" });
  });

  it("is empty when everything is selected", () => {
    expect(availableSkillOptions([skill("a", "tdd", "TDD")], ["a"])).toEqual([]);
  });
});
