import { describe, expect, it } from "vitest";

import type { PackArtifactSummary, PackSummary } from "@/api/packs";
import { packsWithAgents, packsWithSkills, skillArtifacts } from "./newAgentPicker";

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

function art(over: Partial<PackArtifactSummary>): PackArtifactSummary {
  return { kind: over.kind ?? "Agent", id: over.id ?? "i", slug: over.slug ?? "s", name: over.name ?? "N", description: null, sourcePath: null };
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

describe("skillArtifacts", () => {
  const arts = [
    art({ id: "1", kind: "Skill", slug: "tdd", name: "TDD" }),
    art({ id: "2", kind: "Skill", slug: "systematic-debugging", name: "Systematic Debugging" }),
    art({ id: "3", kind: "Agent", slug: "reviewer", name: "Reviewer" }),
  ];

  it("returns only skills (never an agent) when the query is empty", () => {
    expect(skillArtifacts(arts, "  ").map((a) => a.id)).toEqual(["1", "2"]);
  });

  it("filters by name or handle, case-insensitive, and never matches an agent", () => {
    expect(skillArtifacts(arts, "DEBUG").map((a) => a.id)).toEqual(["2"]);
    expect(skillArtifacts(arts, "tdd").map((a) => a.id)).toEqual(["1"]);
    expect(skillArtifacts(arts, "reviewer")).toEqual([]);
  });
});
