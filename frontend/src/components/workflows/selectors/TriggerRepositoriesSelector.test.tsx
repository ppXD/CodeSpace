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
 *   1. value-contract: incoming value is the array; outgoing emits the array
 *      or undefined (matcher empty-config → match-all path);
 *   2. legacy auto-migration (the wrapping-object case is handled by the route,
 *      not this selector);
 *   3. add / remove rows emit the correct shape;
 *   4. project cascade narrows the repo dropdown.
 *
 * Hooks mocked: useProjects + useRepositories. The real implementations call
 * @tanstack/react-query which would need a QueryClient + fixtures here;
 * mocking keeps the test focused on the picker's own logic.
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
  it("renders the empty-list hint when value is undefined", () => {
    // Card renders Add button only — no rows; footer hint explains the
    // empty-list = match-all semantic.
    render(<TriggerRepositoriesSelector value={undefined} onChange={() => {}} />);

    expect(screen.queryAllByTestId("trigger-repositories-row")).toHaveLength(0);
    expect(screen.getByText(/Leave list empty to trigger on any repo/i)).toBeInTheDocument();
  });

  it("renders the empty-list hint when value is an empty array", () => {
    render(<TriggerRepositoriesSelector value={[]} onChange={() => {}} />);

    expect(screen.queryAllByTestId("trigger-repositories-row")).toHaveLength(0);
  });

  it("Add repository button is always visible (even with no rows yet)", () => {
    render(<TriggerRepositoriesSelector value={[]} onChange={() => {}} />);

    expect(screen.getByRole("button", { name: /Add repository/i })).toBeInTheDocument();
  });

  it("each row exposes the explicit field labels from the UI spec", () => {
    // Project: / Repository: / Labels (PR must carry all): — pinned so a
    // visual rewrite can't accidentally drop the contract that tells the
    // operator what the row's filter actually does.
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1" }]} onChange={() => {}} />);

    expect(screen.getByText(/^Project:$/)).toBeInTheDocument();
    expect(screen.getByText(/^Repository:$/)).toBeInTheDocument();
    expect(screen.getByText(/Labels \(PR must carry all\):/)).toBeInTheDocument();
  });

  it("row with no labels shows the (none) placeholder", () => {
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1" }]} onChange={() => {}} />);

    expect(screen.getByText("(none)")).toBeInTheDocument();
  });

  it("renders a row per entry when value is the new array shape", () => {
    const value = [{ repositoryId: "repo-1" }, { repositoryId: "repo-3", labels: ["wip"] }];

    render(<TriggerRepositoriesSelector value={value} onChange={() => {}} />);

    expect(screen.queryAllByTestId("trigger-repositories-row")).toHaveLength(2);
    expect(screen.getByText("wip")).toBeInTheDocument();
  });

  it("Add repository appends an empty row and emits onChange with the array shape", () => {
    const onChange = vi.fn();
    render(<TriggerRepositoriesSelector value={[]} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Add repository/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    // The new empty-id row survives — without it, the user couldn't pick a repo
    // on the row they just added (re-render would strip it before they finished).
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "" }]);
  });

  it("removing a row emits the remaining entries in onChange", () => {
    const onChange = vi.fn();
    const value = [{ repositoryId: "repo-1" }, { repositoryId: "repo-3" }];

    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    const removeButtons = screen.getAllByRole("button", { name: /Remove repository/i });
    fireEvent.click(removeButtons[0]!);

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "repo-3" }]);
  });

  it("removing the LAST row emits undefined so the SchemaForm drops the repositories key", () => {
    // Wire-format contract: empty rows = match every repo. We emit `undefined`
    // so the SchemaForm spreads `repositories: undefined`, dropping the key on
    // JSON serialise → matcher empty-config → match-all path (rule #4).
    // Without this, the picker would emit `[]` which the matcher treats as
    // "explicit no repos, match nothing" (rule #1 with zero entries) — the
    // opposite of the operator's likely intent when they clear the list.
    const onChange = vi.fn();
    const value = [{ repositoryId: "repo-1" }];

    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Remove repository/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toBeUndefined();
  });

  it("picking a project filters the repo dropdown to that project's repos", () => {
    // Two cascade pathways to verify:
    //   (1) Operator picks the project EXPLICITLY → repo dropdown filters immediately.
    //   (2) Operator opens an existing config with a picked repo → project is INFERRED
    //       from the repo, dropdown still filtered correctly.
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "" }]} onChange={vi.fn()} />);

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
    render(<TriggerRepositoriesSelector value={[{ repositoryId: "repo-1" }]} onChange={vi.fn()} />);
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
    // saved config would carry a repo that the picker visually hides.
    const onChange = vi.fn();
    const value = [{ repositoryId: "repo-3" }];   // repo-3 is in Beta
    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    const row = screen.getByTestId("trigger-repositories-row");
    const projectSelect = within(row).getByLabelText("Project") as HTMLSelectElement;
    fireEvent.change(projectSelect, { target: { value: "proj-alpha" } });

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "" }]);
  });

  it("typing a label and pressing Enter adds a chip and emits onChange", () => {
    const onChange = vi.fn();
    const value = [{ repositoryId: "repo-1" }];

    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    const labelInput = screen.getByPlaceholderText("Add label…") as HTMLInputElement;
    fireEvent.change(labelInput, { target: { value: "needs-review" } });
    fireEvent.keyDown(labelInput, { key: "Enter" });

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "repo-1", labels: ["needs-review"] }]);
  });

  it("removing a label chip emits onChange with the remaining labels", () => {
    const onChange = vi.fn();
    const value = [{ repositoryId: "repo-1", labels: ["bug", "wip"] }];

    render(<TriggerRepositoriesSelector value={value} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: /Remove label bug/i }));

    expect(onChange).toHaveBeenCalledTimes(1);
    expect(onChange.mock.calls[0]![0]).toEqual([{ repositoryId: "repo-1", labels: ["wip"] }]);
  });

  it("malformed entries in the array are dropped silently (defensive)", () => {
    // A hand-edited DB row could carry garbage; the picker MUST tolerate it.
    const value = [
      { repositoryId: "repo-1" },
      null,
      "string-not-object",
      { labels: ["orphan"] },           // no repositoryId key
      { repositoryId: 42 },             // non-string repositoryId
      { repositoryId: "repo-2", labels: ["bug"] },
    ];

    render(<TriggerRepositoriesSelector value={value} onChange={() => {}} />);

    const rows = screen.queryAllByTestId("trigger-repositories-row");
    expect(rows).toHaveLength(2);
    expect(screen.getByText("bug")).toBeInTheDocument();
  });
});
