import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { RelatedRepositoriesEditor } from "./RelatedRepositoriesEditor";

/**
 * The agent.code "Add related repositories" editor (multi-repo PR5). Authors `inputs.relatedRepositories`
 * (Array<{ repositoryId, alias?, access }>) that the backend folds into AgentTask.Workspace.
 *
 * Tests pin:
 *   1. empty list → no rows + the single-repo hint;
 *   2. value/onChange contract is RelatedRepoEntry[] | undefined;
 *   3. removing the LAST row emits undefined (key drops → single-repo byte-identical) — unlike the trigger picker;
 *   4. project cascade filters the repo dropdown;
 *   5. alias + access edits emit the right shape;
 *   6. malformed entries are dropped.
 *
 * Hooks mocked: useProjects + useRepositories.
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

describe("RelatedRepositoriesEditor", () => {
  it("empty value renders no rows + the single-repo hint", () => {
    render(<RelatedRepositoriesEditor value={[]} onChange={() => {}} />);

    expect(screen.queryAllByTestId("related-repositories-row")).toHaveLength(0);
    expect(screen.getByText(/single-repo run/i)).toBeInTheDocument();
  });

  it("populated value renders a row per entry", () => {
    render(<RelatedRepositoriesEditor value={[{ repositoryId: "repo-1", access: "read" }]} onChange={() => {}} />);

    expect(screen.queryAllByTestId("related-repositories-row")).toHaveLength(1);
  });

  it("Add appends an empty-id read row", () => {
    const onChange = vi.fn();
    render(<RelatedRepositoriesEditor value={[]} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Add related repository/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "", access: "read" }]);
  });

  it("removing the LAST row emits undefined (key drops → single-repo byte-identical)", () => {
    const onChange = vi.fn();
    render(<RelatedRepositoriesEditor value={[{ repositoryId: "repo-1", access: "read" }]} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Remove related repository/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toBeUndefined();
  });

  it("picking a project filters the repo dropdown", () => {
    render(<RelatedRepositoriesEditor value={[{ repositoryId: "", access: "read" }]} onChange={vi.fn()} />);

    const row = screen.getByTestId("related-repositories-row");
    const projectSelect = within(row).getByLabelText("Project") as HTMLSelectElement;
    let repoSelect = within(row).getByLabelText("Repository") as HTMLSelectElement;
    expect(Array.from(repoSelect.options).map((o) => o.value).filter(Boolean)).toEqual(["repo-1", "repo-2", "repo-3"]);

    fireEvent.change(projectSelect, { target: { value: "proj-beta" } });

    repoSelect = within(row).getByLabelText("Repository") as HTMLSelectElement;
    expect(Array.from(repoSelect.options).map((o) => o.value).filter(Boolean)).toEqual(["repo-3"]);
  });

  it("typing an alias emits it on the entry", () => {
    const onChange = vi.fn();
    render(<RelatedRepositoriesEditor value={[{ repositoryId: "repo-1", access: "read" }]} onChange={onChange} />);

    fireEvent.change(screen.getByLabelText("Alias"), { target: { value: "api" } });

    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "repo-1", alias: "api", access: "read" }]);
  });

  it("changing access to write emits it", () => {
    const onChange = vi.fn();
    render(<RelatedRepositoriesEditor value={[{ repositoryId: "repo-1", access: "read" }]} onChange={onChange} />);

    fireEvent.change(screen.getByLabelText("Access"), { target: { value: "write" } });

    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "repo-1", access: "write" }]);
  });

  it("malformed entries are dropped (defensive)", () => {
    const value = [
      { repositoryId: "repo-1", access: "read" },
      null,
      "not-an-object",
      { repositoryId: 42 },
      { repositoryId: "repo-2", access: "write" },
    ];

    render(<RelatedRepositoriesEditor value={value} onChange={() => {}} />);

    // null / non-object are dropped; { repositoryId: 42 } keeps an empty-id in-progress row.
    expect(screen.queryAllByTestId("related-repositories-row")).toHaveLength(3);
  });
});
