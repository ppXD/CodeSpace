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

  it("picking an option sets that dimension to a one-element list (single-select → list on the wire)", () => {
    const onChange = renderBar();

    fireEvent.click(screen.getByText("Repository"));        // open the popover
    fireEvent.click(screen.getByText("acme/api"));          // choose a repo

    expect(onChange).toHaveBeenCalledWith({ repositoryIds: ["r1"] });
  });

  it("AND-composes with an already-armed dimension rather than replacing it", () => {
    const onChange = renderBar({ runKinds: ["task"] });

    fireEvent.click(screen.getByText("Repository"));
    fireEvent.click(screen.getByText("acme/api"));

    expect(onChange).toHaveBeenCalledWith({ runKinds: ["task"], repositoryIds: ["r1"] });
  });

  it("the inline ✕ is a real, keyboard-operable button that clears just its own dimension", () => {
    const onChange = renderBar({ repositoryIds: ["r1"], runKinds: ["task"] });

    const clear = screen.getByLabelText("Clear Repository");
    expect(clear.tagName).toBe("BUTTON");   // not a <span role="button"> nested in the pill button (invalid + keyboard-dead)

    fireEvent.click(clear);
    expect(onChange).toHaveBeenCalledWith({ repositoryIds: undefined, runKinds: ["task"] });
  });

  it("Clear resets every scope dimension at once", () => {
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
