import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ScopeSuggestion } from "../scope-introspection";
import { RepositoryWorkspacePicker } from "./RepositoryWorkspacePicker";

vi.mock("@/hooks/use-projects", () => ({ useProjects: () => ({ data: [] }) }));
vi.mock("@/hooks/use-repositories", () => ({ useRepositories: () => ({ data: [] }) }));
vi.mock("../VariablePickerInput", () => ({
  VariablePickerInput: ({ value, onChange, placeholder }: { value: string; onChange: (next: string) => void; placeholder?: string }) => (
    <input aria-label={placeholder} value={value} onChange={(event) => onChange(event.target.value)} />
  ),
}));

const suggestions: ScopeSuggestion[] = [{ path: "trigger.repositoryId", label: "trigger.repositoryId", category: "trigger" }];

describe("RepositoryWorkspacePicker expression mode", () => {
  it("opens in Expression mode for a repository reference and edits only the primary repository id", () => {
    const onChange = vi.fn();
    render(<RepositoryWorkspacePicker repositoryId="{{trigger.repositoryId}}" relatedRepositories={[{ repositoryId: "related-1" }]} drafts={undefined} suggestions={suggestions} onChange={onChange} />);

    expect(screen.getByRole("button", { name: "Expression" })).toHaveAttribute("data-active", "true");
    const expression = screen.getByRole("textbox", { name: "Type @ to reference an input or step output" });
    fireEvent.change(expression, { target: { value: "{{trigger.otherRepositoryId}}" } });

    expect(onChange).toHaveBeenCalledWith({
      repositoryId: "{{trigger.otherRepositoryId}}",
      relatedRepositories: [{ repositoryId: "related-1", access: "read" }],
      workspaceRepoDrafts: undefined,
    });
  });

  it("keeps the repository picker in Pick mode for a literal id", () => {
    render(<RepositoryWorkspacePicker repositoryId="repo-1" relatedRepositories={undefined} drafts={undefined} suggestions={suggestions} onChange={vi.fn()} />);

    expect(screen.getByRole("button", { name: "Pick" })).toHaveAttribute("data-active", "true");
    expect(screen.getByTestId("workspace-primary-row")).toBeInTheDocument();
  });
});
