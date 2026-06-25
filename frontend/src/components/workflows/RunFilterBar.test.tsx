import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { RunListFilterInput } from "@/api/workflows";

vi.mock("@/hooks/use-repositories", () => ({
  useRepositories: () => ({ data: [{ id: "r1", name: "acme/api" }, { id: "r2", name: "acme/web" }], isLoading: false }),
}));
vi.mock("@/hooks/use-projects", () => ({ useProjects: () => ({ data: [{ id: "p1", name: "Platform" }], isLoading: false }) }));
vi.mock("@/hooks/use-team-members", () => ({ useTeamMembers: () => ({ data: [{ userId: "u1", name: "Mars" }], isLoading: false }) }));
vi.mock("@/hooks/use-agents", () => ({ useAgentDefinitions: () => ({ data: [{ id: "a1", name: "Reviewer" }], isLoading: false }) }));

import { RunFilterBar } from "./RunFilterBar";

function renderBar(filter: RunListFilterInput = {}) {
  const onChange = vi.fn();
  render(<RunFilterBar filter={filter} onChange={onChange} />);
  return onChange;
}

describe("RunFilterBar", () => {
  it("renders a pill per scope dimension", () => {
    renderBar();
    for (const d of ["Kind", "Repository", "Project", "Launched by", "Agent"]) {
      expect(screen.getByText(d)).toBeInTheDocument();
    }
  });

  it("picking an option arms that dimension as a one-element list", () => {
    const onChange = renderBar();

    fireEvent.click(screen.getByText("Repository"));        // open the popover
    fireEvent.click(screen.getByText("acme/api"));          // tick a repo

    expect(onChange).toHaveBeenCalledWith({ repositoryIds: ["r1"] });
  });

  it("multi-select: a second pick ADDS to the facet's list (OR within a facet)", () => {
    const onChange = renderBar({ repositoryIds: ["r1"] });   // acme/api already chosen

    fireEvent.click(screen.getByText("Repository"));
    fireEvent.click(screen.getByText("acme/web"));           // add the second repo

    expect(onChange).toHaveBeenCalledWith({ repositoryIds: ["r1", "r2"] });
  });

  it("toggling an already-chosen value removes just it from the facet", () => {
    const onChange = renderBar({ repositoryIds: ["r1", "r2"] });

    fireEvent.click(screen.getByText("Repository"));
    fireEvent.click(screen.getByText("acme/api"));           // r1 was on → toggle off

    expect(onChange).toHaveBeenCalledWith({ repositoryIds: ["r2"] });
  });

  it("the armed pill shows a count of how many values are picked", () => {
    renderBar({ repositoryIds: ["r1", "r2"] });
    const pill = screen.getByText("Repository").closest(".filterpill")!;
    expect(pill.querySelector(".filterpill-count")?.textContent).toBe("2");
  });

  it("AND-composes with an already-armed dimension rather than replacing it", () => {
    const onChange = renderBar({ runKinds: ["task"] });

    fireEvent.click(screen.getByText("Repository"));
    fireEvent.click(screen.getByText("acme/api"));

    expect(onChange).toHaveBeenCalledWith({ runKinds: ["task"], repositoryIds: ["r1"] });
  });

  it("the popover's per-facet Clear drops just that dimension to undefined", () => {
    const onChange = renderBar({ repositoryIds: ["r1", "r2"], runKinds: ["task"] });

    fireEvent.click(screen.getByText("Repository"));         // open the popover
    fireEvent.click(screen.getByText("Clear"));              // the header Clear (exact — the bar's reads "Clear (2)")

    expect(onChange).toHaveBeenCalledWith({ repositoryIds: undefined, runKinds: ["task"] });
  });

  it("the bar Clear resets every scope dimension at once", () => {
    const onChange = renderBar({ repositoryIds: ["r1"], runKinds: ["task"] });

    fireEvent.click(screen.getByText(/^Clear/));

    expect(onChange).toHaveBeenCalledWith({
      runKinds: undefined, repositoryIds: undefined, projectIds: undefined, actorIds: undefined, agentDefinitionIds: undefined,
    });
  });

  it("shows no Clear control when nothing is armed", () => {
    renderBar();
    expect(screen.queryByText(/^Clear/)).not.toBeInTheDocument();
  });
});
