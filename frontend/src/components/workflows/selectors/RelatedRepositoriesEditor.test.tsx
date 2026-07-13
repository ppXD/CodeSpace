import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { RelatedRepositoriesEditor } from "./RelatedRepositoriesEditor";

/**
 * The agent.run "Add related repositories" editor (multi-repo PR5). Authors `inputs.relatedRepositories`
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

    // The repo combobox initially offers every repo (by fullPath).
    fireEvent.focus(within(row).getByRole("textbox", { name: "Pick a repository…" }));
    expect(within(row).getByRole("option", { name: "acme/api" })).toBeInTheDocument();
    expect(within(row).getByRole("option", { name: "labs/exp" })).toBeInTheDocument();

    // Narrow by project Beta → the repo list drops to that project's repos only.
    fireEvent.focus(within(row).getByRole("textbox", { name: "All projects" }));
    fireEvent.mouseDown(within(row).getByRole("option", { name: "Beta" }));

    fireEvent.focus(within(row).getByRole("textbox", { name: "Pick a repository…" }));
    expect(within(row).getByRole("option", { name: "labs/exp" })).toBeInTheDocument();
    expect(within(row).queryByRole("option", { name: "acme/api" })).toBeNull();
  });

  it("a stale project draft never hides a real repo after the value changes externally", () => {
    const { rerender } = render(<RelatedRepositoriesEditor value={[{ repositoryId: "", access: "read" }]} onChange={vi.fn()} />);

    // Draft-narrow the empty row to project Beta (its repositoryId is still empty — a pure UI draft).
    const row0 = screen.getByTestId("related-repositories-row");
    fireEvent.focus(within(row0).getByRole("textbox", { name: "All projects" }));
    fireEvent.mouseDown(within(row0).getByRole("option", { name: "Beta" }));

    // The config is replaced from OUTSIDE (node switch / undo) with a repo that belongs to project Alpha.
    rerender(<RelatedRepositoriesEditor value={[{ repositoryId: "repo-1", access: "read" }]} onChange={vi.fn()} />);

    // Regression: the restored repo resolves to its own label — the stale Beta draft must not filter it out
    // (previously projectForRow returned the draft, so repo-1 fell out of the dropdown and rendered "Unavailable").
    const row = screen.getByTestId("related-repositories-row");
    expect(within(row).getByText("acme/api")).toBeInTheDocument();
    expect(within(row).queryByText("Unavailable")).toBeNull();
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

  it("malformed entries are dropped + survivors keep their content + order", () => {
    const onChange = vi.fn();
    const value = [
      { repositoryId: "repo-1", access: "read" },
      null,
      "not-an-object",
      { repositoryId: 42 },                          // non-string id → coerced to an empty-id in-progress row
      { repositoryId: "repo-2", access: "write" },
    ];

    render(<RelatedRepositoriesEditor value={value} onChange={onChange} />);

    const rows = screen.getAllByTestId("related-repositories-row");
    expect(rows).toHaveLength(3);   // null + non-object dropped; 3 survive

    // The surviving entries keep their repo (chip) + access (select), in order.
    expect(within(rows[0]!).getByText("acme/api")).toBeInTheDocument();                    // repo-1
    expect((within(rows[0]!).getByLabelText("Access") as HTMLSelectElement).value).toBe("read");
    expect(within(rows[1]!).queryByText(/acme|labs/)).toBeNull();                          // coerced empty-id → no repo chip
    expect(within(rows[2]!).getByText("acme/web")).toBeInTheDocument();                    // repo-2
    expect((within(rows[2]!).getByLabelText("Access") as HTMLSelectElement).value).toBe("write");
  });
});
