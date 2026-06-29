import { describe, expect, it } from "vitest";

import type { PackArtifactSummary, PackSummary } from "@/api/packs";
import { agentArtifacts, packsWithAgents } from "./newAgentPicker";

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

describe("agentArtifacts", () => {
  const arts = [
    art({ id: "1", kind: "Agent", slug: "code-reviewer", name: "Code Reviewer" }),
    art({ id: "2", kind: "Agent", slug: "backend-architect", name: "Backend Architect" }),
    art({ id: "3", kind: "Skill", slug: "tdd", name: "TDD" }),
  ];

  it("returns only agents (never a skill) when the query is empty", () => {
    expect(agentArtifacts(arts, "  ").map((a) => a.id)).toEqual(["1", "2"]);
  });

  it("filters by name or handle, case-insensitive, and never matches a skill", () => {
    expect(agentArtifacts(arts, "ARCHITECT").map((a) => a.id)).toEqual(["2"]);
    expect(agentArtifacts(arts, "code-rev").map((a) => a.id)).toEqual(["1"]);
    expect(agentArtifacts(arts, "tdd")).toEqual([]);
  });
});
