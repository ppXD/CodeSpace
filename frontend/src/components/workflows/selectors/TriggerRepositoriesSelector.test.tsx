import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { TriggerRepositoriesSelector } from "./TriggerRepositoriesSelector";

/**
 * The selector is the user-facing surface for the <c>repositories</c> array of
 * the PR-trigger activation config (PR #23). It's dispatched via
 * <c>x-selector: "trigger.repositories"</c> on the <c>repositories</c> property
 * of the trigger node ConfigSchema — so the SchemaForm passes the array value
 * directly, NOT the wrapping config object.
 *
 * Tests pin:
 *   1. Safe default — empty list = match nothing (NOT match all);
 *   2. "Match every repository" checkbox is the only path to undefined emit;
 *   3. Value/onChange contract is `TriggerRepoEntry[] | undefined`;
 *   4. Add / remove rows emit the correct shape;
 *   5. Project cascade filters the repo dropdown.
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

describe("TriggerRepositoriesSelector — safe default + match-all checkbox", () => {
  it("empty array value: checkbox unchecked, list visible, footer warns trigger fires on nothing", () => {
    // The new safe default. A fresh trigger lands here (schema default
    // `repositories: []`). NO PR fires the workflow until the operator either
    // adds a row or checks Match-every-repository.
    render(<TriggerRepositoriesSelector value={[]} onChange={() => {}} />);

    const checkbox = screen.getByRole("checkbox", { name: /Match every repository/i }) as HTMLInputElement;
    expect(checkbox.checked).toBe(false);
    expect(screen.getByTestId("trigger-repositories-list")).toBeInTheDocument();
    expect(screen.getByText(/this trigger fires on nothing yet/i)).toBeInTheDocument();
  });

  it("undefined value: checkbox checked, list hidden, footer confirms team-wide trigger", () => {
    // Wire-format `undefined` (saved as the `repositories` key being absent)
    // is the EXPLICIT match-all state. Operator either checked the box in the
    // UI, or an API caller omitted the key. Either way, the picker reflects it.
    render(<TriggerRepositoriesSelector value={undefined} onChange={() => {}} />);

    const checkbox = screen.getByRole("checkbox", { name: /Match every repository/i }) as HTMLInputElement;
    expect(checkbox.checked).toBe(true);
    expect(screen.queryByTestId("trigger-repositories-list")).not.toBeInTheDocument();
    expect(screen.getByText(/fires on PRs from every repository/i)).toBeInTheDocument();
  });

  it("populated array value: checkbox unchecked, rows render", () => {
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1" }]} onChange={() => {}} />);

    const checkbox = screen.getByRole("checkbox", { name: /Match every repository/i }) as HTMLInputElement;
    expect(checkbox.checked).toBe(false);
    expect(screen.queryAllByTestId("trigger-repositories-row")).toHaveLength(1);
  });

  it("checking the box emits undefined (match-all opt-in)", () => {
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={[]} onChange={onChange} />);

    const checkbox = screen.getByRole("checkbox", { name: /Match every repository/i });
    fireEvent.click(checkbox);

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toBeUndefined();
  });

  it("unchecking the box emits [] — back to safe default (match nothing)", () => {
    // Critical contract: toggling OFF Match-all returns to "explicit empty
    // list = no match" instead of leaving the workflow firing on everything
    // by accident.
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={undefined} onChange={onChange} />);

    const checkbox = screen.getByRole("checkbox", { name: /Match every repository/i });
    fireEvent.click(checkbox);

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([]);
  });

  it("Add repository button appends an empty-id row, emitting an array (NOT undefined)", () => {
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={[]} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Add repository/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "" }]);
  });

  it("removing the LAST row emits [] (NOT undefined) — preserves safe default", () => {
    // This is the safe-default invariant: the ONLY path to undefined is via
    // the checkbox. Clearing all rows lands on `[]` (match nothing) so the
    // workflow doesn't silently start firing on every repo.
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1" }]} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Remove repository/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([]);
  });

  it("each row exposes the explicit field labels from the UI spec", () => {
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1" }]} onChange={() => {}} />);

    expect(screen.getByText(/^Project:$/)).toBeInTheDocument();
    expect(screen.getByText(/^Repository:$/)).toBeInTheDocument();
    expect(screen.getByText(/Labels \(PR must carry all\):/)).toBeInTheDocument();
  });

  it("row with no labels shows the (none) placeholder", () => {
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1" }]} onChange={() => {}} />);

    expect(screen.getByText("(none)")).toBeInTheDocument();
  });

  it("removing one of many rows emits the remaining entries", () => {
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1" }, { repositoryId: "repo-3" }]} onChange={onChange} />);

    const removeButtons = screen.getAllByRole("button", { name: /Remove repository/i });
    fireEvent.click(removeButtons[0]!);

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "repo-3" }]);
  });

  it("picking a project filters the repo dropdown to that project's repos", () => {
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "" }]} onChange={vi.fn()} />);

    const row = screen.getByTestId("trigger-repositories-row");
    const projectSelect = within(row).getByLabelText("Project") as HTMLSelectElement;
    let repoSelect = within(row).getByLabelText("Repository") as HTMLSelectElement;

    expect(Array.from(repoSelect.options).map((o) => o.value).filter(Boolean)).toEqual(["repo-1", "repo-2", "repo-3"]);

    fireEvent.change(projectSelect, { target: { value: "proj-alpha" } });

    repoSelect = within(row).getByLabelText("Repository") as HTMLSelectElement;
    const afterPick = Array.from(repoSelect.options).map((o) => o.value).filter(Boolean);
    expect(afterPick).toEqual(["repo-1", "repo-2"]);
    expect(projectSelect.value).toBe("proj-alpha");
  });

  it("picking a project clears the row's repo when it doesn't belong to that project", () => {
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-3" }]} onChange={onChange} />);

    const row = screen.getByTestId("trigger-repositories-row");
    const projectSelect = within(row).getByLabelText("Project") as HTMLSelectElement;
    fireEvent.change(projectSelect, { target: { value: "proj-alpha" } });

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "" }]);
  });

  it("typing a label and pressing Enter adds a chip", () => {
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1" }]} onChange={onChange} />);

    const labelInput = screen.getByPlaceholderText("Add label…") as HTMLInputElement;
    fireEvent.change(labelInput, { target: { value: "needs-review" } });
    fireEvent.keyDown(labelInput, { key: "Enter" });

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "repo-1", labels: ["needs-review"] }]);
  });

  it("removing a label chip emits the remaining labels", () => {
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1", labels: ["bug", "wip"] }]} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Remove label bug/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "repo-1", labels: ["wip"] }]);
  });

  it("malformed entries are dropped silently (defensive)", () => {
    const value = [
      { repositoryId: "repo-1" },
      null,
      "string-not-object",
      { labels: ["orphan"] },
      { repositoryId: 42 },
      { repositoryId: "repo-2", labels: ["bug"] },
    ];

    render(<TriggerRepositoriesSelector value={value} onChange={() => {}} />);

    expect(screen.queryAllByTestId("trigger-repositories-row")).toHaveLength(2);
    expect(screen.getByText("bug")).toBeInTheDocument();
  });
});
