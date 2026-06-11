import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AgentPaletteSection } from "./AgentPaletteSection";

const agents = vi.hoisted(() => ({ data: [{ id: "p1", slug: "reviewer", name: "Reviewer" }, { id: "p2", slug: "architect", name: "" }] as { id: string; slug: string; name: string }[] }));

vi.mock("@/hooks/use-agents", () => ({ useAgentDefinitions: () => ({ isLoading: false, data: agents.data }) }));

describe("AgentPaletteSection", () => {
  it("renders nothing when the agent.code node isn't registered", () => {
    const { container } = render(<AgentPaletteSection enabled={false} onAdd={() => {}} />);
    expect(container).toBeEmptyDOMElement();
  });

  it("lists personas (name or @handle) and emits the chosen persona id on click", () => {
    const onAdd = vi.fn();
    render(<AgentPaletteSection enabled onAdd={onAdd} />);

    expect(screen.getByText("Reviewer")).toBeInTheDocument();          // named persona
    expect(screen.getByText("@reviewer")).toBeInTheDocument();         // its @handle subtitle
    expect(screen.getAllByText("@architect")).toHaveLength(2);         // unnamed → name AND subtitle both fall back to the handle

    fireEvent.click(screen.getByText("Reviewer"));
    expect(onAdd).toHaveBeenCalledWith("p1");
  });

  it("renders nothing when the team has no personas", () => {
    const saved = agents.data;
    agents.data = [];
    const { container } = render(<AgentPaletteSection enabled onAdd={() => {}} />);
    expect(container).toBeEmptyDOMElement();
    agents.data = saved;
  });
});
