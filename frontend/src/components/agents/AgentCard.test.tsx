import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentDefinitionSummary } from "@/api/agents";

import { AgentCard } from "./AgentCard";

function agent(over: Partial<AgentDefinitionSummary> = {}): AgentDefinitionSummary {
  return {
    id: "a1", teamId: "t", slug: "reviewer", name: "Reviewer", description: null, systemPrompt: "",
    model: null, defaultAutonomy: null, tools: null, origin: "Authored", packName: null,
    boundSkills: [], createdDate: "2026-01-01T00:00:00Z", ...over,
  };
}

describe("AgentCard skill tokens", () => {
  it("shows each bound skill's NAME, not its disambiguated -2/-3 handle", () => {
    render(<AgentCard agent={agent({ boundSkills: [{ skillDefinitionId: "s1", slug: "tdd-2", name: "TDD" }] })} onOpen={() => {}} />);

    expect(screen.getByText("TDD")).toBeInTheDocument();           // friendly name on the token
    expect(screen.queryByText("@tdd-2")).not.toBeInTheDocument();  // the climbing handle is not the visible label
  });

  it("shows the empty hint when no skills are bound", () => {
    render(<AgentCard agent={agent({ boundSkills: [] })} onOpen={() => {}} />);
    expect(screen.getByText(/No skills bound/)).toBeInTheDocument();
  });
});
