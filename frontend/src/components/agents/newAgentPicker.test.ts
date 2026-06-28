import { describe, expect, it } from "vitest";

import type { PackSummary } from "@/api/packs";
import { packsWithAgents, packsWithSkills } from "./newAgentPicker";

function pack(over: Partial<PackSummary>): PackSummary {
  return {
    id: over.id ?? "p",
    kind: "Github",
    name: over.name ?? "n",
    url: null,
    reference: null,
    lastSyncedSha: null,
    lastSyncedDate: null,
    agentCount: over.agentCount ?? 0,
    skillCount: over.skillCount ?? 0,
  };
}

describe("packsWithAgents", () => {
  it("keeps only packs with at least one agent", () => {
    const result = packsWithAgents([pack({ id: "skills-only", agentCount: 0, skillCount: 3 }), pack({ id: "has-agents", agentCount: 2 })]);
    expect(result.map((p) => p.id)).toEqual(["has-agents"]);
  });
});

describe("packsWithSkills", () => {
  it("keeps only packs with at least one skill", () => {
    const result = packsWithSkills([pack({ id: "agents-only", agentCount: 2, skillCount: 0 }), pack({ id: "has-skills", skillCount: 4 })]);
    expect(result.map((p) => p.id)).toEqual(["has-skills"]);
  });
});
