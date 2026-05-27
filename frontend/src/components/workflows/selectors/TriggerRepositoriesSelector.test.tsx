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
    // Two cascade pathways to verify:
    //   (1) Operator picks the project EXPLICITLY → repo dropdown filters immediately.
    //   (2) Operator opens an existing config with a picked repo → project is INFERRED
    //       from the repo, dropdown still filtered correctly.
    const value = { repositories: [{ repositoryId: "" }] };
    render(<TriggerRepositoriesSelector value={value} onChange={vi.fn()} />);

    const row = screen.getByTestId("trigger-repositories-row");
    const projectSelect = within(row).getByLabelText("Project") as HTMLSelectElement;
    let repoSelect = within(row).getByLabelText("Repository") as HTMLSelectElement;

    // Pre-cascade: every repo across all projects is selectable.
    expect(Array.from(repoSelect.options).map((o) => o.value).filter(Boolean)).toEqual(["repo-1", "repo-2", "repo-3"]);

    // (1) Explicit pick — Alpha contains repo-1 + repo-2, NOT repo-3.
    fireEvent.change(projectSelect, { target: { value: "proj-alpha" } });

    repoSelect = within(row).getByLabelText("Repository") as HTMLSelectElement;
    const afterPickValues = Array.from(repoSelect.options).map((o) => o.value).filter(Boolean);
    expect(afterPickValues).toEqual(["repo-1", "repo-2"]);
    expect(afterPickValues).not.toContain("repo-3");
    // Project select retains the Alpha pick so the operator can SEE which filter is
    // active — without this they'd be looking at "All projects" while the dropdown
    // was actually filtered, which is confusing UX.
    expect(projectSelect.value).toBe("proj-alpha");

    // (2) Inferred from existing repo — re-render with repo-1 pre-picked, project
    // should default to Alpha and only Alpha repos should be visible.
    render(<TriggerRepositoriesSelector value={{ repositories: [{ repositoryId: "repo-1" }] }} onChange={vi.fn()} />);
    const rows = screen.getAllByTestId("trigger-repositories-row");
    const inferredRow = rows[rows.length - 1]!;
    const inferredRepoValues = Array.from(
      (within(inferredRow).getByLabelText("Repository") as HTMLSelectElement).options,
    ).map((o) => o.value).filter(Boolean);
    expect(inferredRepoValues).toEqual(["repo-1", "repo-2"]);
  });

  it("picking a project clears the row's repo so a stale cross-project repo can't persist", () => {
    // Edge case: row currently points at a Beta repo; operator picks Alpha. The
    // existing repo doesn't belong to Alpha, so we MUST clear it — otherwise the
    // saved config would carry a repo that the picker visually hides (and the
    // operator can't even see what they just configured).
    const onChange = vi.fn();
    const value = { repositories: [{ repositoryId: "repo-3" }] };   // repo-3 is in Beta
    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    const row = screen.getByTestId("trigger-repositories-row");
    const projectSelect = within(row).getByLabelText("Project") as HTMLSelectElement;
    fireEvent.change(projectSelect, { target: { value: "proj-alpha" } });

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual({ repositories: [{ repositoryId: "" }] });
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
