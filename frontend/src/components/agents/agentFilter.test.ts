import { describe, expect, it } from "vitest";

import type { AgentDefinitionSummary } from "@/api/agents";
import { filterAgents } from "./agentFilter";

function agent(over: Partial<AgentDefinitionSummary>): AgentDefinitionSummary {
  return {
    id: over.id ?? crypto.randomUUID(),
    teamId: "t",
    slug: over.slug ?? "slug",
    name: over.name ?? "Name",
    description: over.description ?? null,
    systemPrompt: "",
    model: null,
    defaultAutonomy: null,
    tools: null,
    origin: over.origin ?? "Authored",
    boundSkills: [],
    createdDate: "2026-01-01T00:00:00Z",
  };
}

const rows: AgentDefinitionSummary[] = [
  agent({ name: "Backend Architect", slug: "backend-architect", description: "Designs APIs", origin: "Authored" }),
  agent({ name: "Code Reviewer", slug: "code-reviewer", description: "Adversarial PR review", origin: "Imported" }),
  agent({ name: "Docs Writer", slug: "docs-writer", description: null, origin: "Authored" }),
];

describe("filterAgents", () => {
  it("returns everything when the query is blank and origin is all", () => {
    expect(filterAgents(rows, "", "all")).toHaveLength(3);
    expect(filterAgents(rows, "   ", "all")).toHaveLength(3);
  });

  it("filters by origin", () => {
    expect(filterAgents(rows, "", "Authored").map((a) => a.slug)).toEqual(["backend-architect", "docs-writer"]);
    expect(filterAgents(rows, "", "Imported").map((a) => a.slug)).toEqual(["code-reviewer"]);
  });

  it("matches the query case-insensitively against name, handle, or description", () => {
    expect(filterAgents(rows, "REVIEWER", "all").map((a) => a.slug)).toEqual(["code-reviewer"]);   // name
    expect(filterAgents(rows, "docs-writer", "all").map((a) => a.slug)).toEqual(["docs-writer"]);  // handle
    expect(filterAgents(rows, "apis", "all").map((a) => a.slug)).toEqual(["backend-architect"]);   // description
  });

  it("does not match against a null description (no throw)", () => {
    expect(filterAgents(rows, "nothing-matches-this", "all")).toHaveLength(0);
  });

  it("combines origin and query (both must hold)", () => {
    expect(filterAgents(rows, "reviewer", "Authored")).toHaveLength(0);   // matches an Imported persona, excluded by origin
    expect(filterAgents(rows, "reviewer", "Imported").map((a) => a.slug)).toEqual(["code-reviewer"]);
  });
});
