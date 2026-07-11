import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AgentSelector } from "./AgentSelector";

/**
 * The agent picker is dispatched via `x-selector: "agent"` on the `agent.run` node's config
 * (`agentDefinitionId`). It lists the team's Agent personas and saves the chosen id as a plain string.
 * Hook mocked: useAgentDefinitions.
 */
vi.mock("@/hooks/use-agents", () => ({
  useAgentDefinitions: () => ({
    isLoading: false,
    data: [
      { id: "a1", slug: "backend-architect", name: "Backend Architect" },
      { id: "a2", slug: "code-reviewer", name: "" },
    ],
  }),
}));

describe("AgentSelector", () => {
  it("lists personas with name + @handle labels and emits the chosen id", () => {
    const onChange = vi.fn();
    render(<AgentSelector value="" onChange={onChange} />);

    expect(screen.getByRole("option", { name: "Backend Architect (@backend-architect)" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "@code-reviewer" })).toBeInTheDocument();   // unnamed → handle only

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "a1" } });
    expect(onChange).toHaveBeenCalledWith("a1");
  });

  it("reflects the currently-selected persona id", () => {
    render(<AgentSelector value="a2" onChange={() => {}} />);
    expect((screen.getByRole("combobox") as HTMLSelectElement).value).toBe("a2");
  });
});
