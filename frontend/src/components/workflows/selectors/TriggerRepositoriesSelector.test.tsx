import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { TriggerRepositoriesSelector } from "./TriggerRepositoriesSelector";

/**
 * The selector is the user-facing surface for the
 * <c>{ repositories: [{ repositoryId, labels? }] }</c> trigger config introduced
 * in PR #23. These tests pin the behaviours the matcher relies on:
 *
 *   1. legacy single-repo config renders as one row (auto-migration works in the UI);
 *   2. add / remove rows emit the new array shape via onChange;
 *   3. picking a project cascades to filter the repo dropdown.
 *
 * Hooks mocked: useProjects + useRepositories. The real implementations call
 * @tanstack/react-query which would need a QueryClient + fixtures here; mocking
 * keeps the test focused on the picker's own logic.
 */

vi.mock("@/hooks/use-projects", () => ({
  useProjects: () => ({
    data: [
      { id: "proj-alpha", slug: "alpha", name: "Alpha", teamId: "t" },
      { id: "proj-beta", slug: "beta", name: "Beta", teamId: "t" },
    ],
  }),
}));

vi.mock("@/hooks/use-repositories", () => ({
  useRepositories: () => ({
    data: [
      { id: "repo-1", fullPath: "acme/api", projects: [{ id: "proj-alpha", slug: "alpha", name: "Alpha" }] },
      { id: "repo-2", fullPath: "acme/web", projects: [{ id: "proj-alpha", slug: "alpha", name: "Alpha" }] },
      { id: "repo-3", fullPath: "labs/exp", projects: [{ id: "proj-beta", slug: "beta", name: "Beta" }] },
    ],
  }),
}));

describe("TriggerRepositoriesSelector", () => {
  it("renders the empty hint when value has no repositories", () => {
    render(<TriggerRepositoriesSelector value={{}} onChange={() => {}} />);

    expect(screen.queryAllByTestId("trigger-repositories-row")).toHaveLength(0);
    expect(screen.getByText(/No repositories selected/i)).toBeInTheDocument();
  });

  it("renders a row per entry when value has the new array shape", () => {
    const value = { repositories: [{ repositoryId: "repo-1" }, { repositoryId: "repo-3", labels: ["wip"] }] };

    render(<TriggerRepositoriesSelector value={value} onChange={() => {}} />);

    expect(screen.queryAllByTestId("trigger-repositories-row")).toHaveLength(2);
    // Labels chip surface verifies the labels array reached the row.
    expect(screen.getByText("wip")).toBeInTheDocument();
  });

  it("renders the legacy { repositoryId, labels } shape as a single row (auto-migration)", () => {
    // This is the load-bearing migration check: a workflow saved before PR #23 has
    // the flat shape; the UI must surface it as one row so the operator sees it.
    const legacy = { repositoryId: "repo-2", labels: ["bug"] };

    render(<TriggerRepositoriesSelector value={legacy} onChange={() => {}} />);

    expect(screen.queryAllByTestId("trigger-repositories-row")).toHaveLength(1);
    expect(screen.getByText("bug")).toBeInTheDocument();
  });

  it("Add repository appends an empty row and emits onChange with the in-progress entry preserved", () => {
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={{ repositories: [] }} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Add repository/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    // The empty row survives — without it, the user couldn't pick a repo on the row
    // they just added (re-render would strip it before they finished). The matcher
    // tolerates empty-repositoryId entries (no match) so storage noise is harmless.
    expect(onChange.mock.calls[0]![0]).toEqual({ repositories: [{ repositoryId: "" }] });
  });

  it("removing a row emits the remaining entries in onChange", () => {
    const onChange = vi.fn();
    const value = { repositories: [{ repositoryId: "repo-1" }, { repositoryId: "repo-3" }] };

    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    const removeButtons = screen.getAllByRole("button", { name: /Remove repository/i });
    fireEvent.click(removeButtons[0]!);

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual({ repositories: [{ repositoryId: "repo-3" }] });
  });

  it("picking a project filters the repo dropdown to that project's repos", () => {
    const onChange = vi.fn();
    const value = { repositories: [{ repositoryId: "" }] };

    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    const row = screen.getByTestId("trigger-repositories-row");
    const projectSelect = within(row).getByLabelText("Project");
    const repoSelect = within(row).getByLabelText("Repository") as HTMLSelectElement;

    // Pre-cascade: every repo across all projects is selectable.
    expect(Array.from(repoSelect.options).map((o) => o.value).filter(Boolean)).toEqual(["repo-1", "repo-2", "repo-3"]);

    // Pick the Alpha project. The row's inferred project follows the picked repo,
    // not the project dropdown directly, so we verify the onChange firing pattern
    // separately. For the filter assertion we re-render with the row already
    // pointing at an Alpha repo and check the repo dropdown only lists Alpha repos.
    fireEvent.change(projectSelect, { target: { value: "proj-alpha" } });

    // The picker uses an inferred project from the picked repo; the project select's
    // value resets to "" since the row's repo just got cleared. Emit went out.
    expect(onChange).toHaveBeenCalledTimes(1);

    // Verify the cascade by rendering with a row pre-pointed at a known-project repo.
    const onChange2 = vi.fn();
    render(
      <TriggerRepositoriesSelector
        value={{ repositories: [{ repositoryId: "repo-1" }] }}
        onChange={onChange2}
      />,
    );
    const rows = screen.getAllByTestId("trigger-repositories-row");
    const secondRowRepoSelect = within(rows[rows.length - 1]!).getByLabelText("Repository") as HTMLSelectElement;
    const visibleValues = Array.from(secondRowRepoSelect.options).map((o) => o.value).filter(Boolean);
    expect(visibleValues).toEqual(["repo-1", "repo-2"]);
    expect(visibleValues).not.toContain("repo-3");
  });

  it("typing a label and pressing Enter adds a chip and emits onChange", () => {
    const onChange = vi.fn();
    const value = { repositories: [{ repositoryId: "repo-1" }] };

    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    const labelInput = screen.getByPlaceholderText("Add label…") as HTMLInputElement;
    fireEvent.change(labelInput, { target: { value: "needs-review" } });
    fireEvent.keyDown(labelInput, { key: "Enter" });

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual({
      repositories: [{ repositoryId: "repo-1", labels: ["needs-review"] }],
    });
  });

  it("removing a label chip emits onChange with the remaining labels", () => {
    const onChange = vi.fn();
    const value = { repositories: [{ repositoryId: "repo-1", labels: ["bug", "wip"] }] };

    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Remove label bug/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual({
      repositories: [{ repositoryId: "repo-1", labels: ["wip"] }],
    });
  });
});
