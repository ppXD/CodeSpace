import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AgentMultiSelector, AgentSelector } from "./AgentSelector";

/**
 * Both agent pickers render the shared SearchSelect combobox and save the persona UUID(s). Single drives
 * `agent.code`'s agentDefinitionId; multi drives the supervisor's allowedAgentDefinitionIds. Hook mocked:
 * useAgentDefinitions.
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

describe("AgentSelector (single)", () => {
  it("lists personas (name · @handle, handle-only when unnamed) and emits the chosen id", () => {
    const onChange = vi.fn();
    render(<AgentSelector value="" onChange={onChange} />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Pick an agent…" }));
    expect(screen.getByRole("option", { name: /Backend Architect/ })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "@code-reviewer" })).toBeInTheDocument();   // unnamed → handle only

    fireEvent.mouseDown(screen.getByRole("option", { name: /Backend Architect/ }));
    expect(onChange).toHaveBeenCalledWith("a1");
  });

  it("shows the selected persona as a chip", () => {
    render(<AgentSelector value="a1" onChange={() => {}} />);
    expect(screen.getByText("Backend Architect")).toBeInTheDocument();
  });
});

describe("AgentMultiSelector", () => {
  it("adds a persona by id and shows the count hint", () => {
    const onChange = vi.fn();
    render(<AgentMultiSelector value={["a1"]} onChange={onChange} />);

    expect(screen.getByText("1 selected — dispatched agents must use one of these.")).toBeInTheDocument();

    fireEvent.focus(screen.getByRole("textbox", { name: "Search agents…" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: "@code-reviewer" }));
    expect(onChange).toHaveBeenCalledWith(["a1", "a2"]);
  });
});
