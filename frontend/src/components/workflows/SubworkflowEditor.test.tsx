import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { SubworkflowEditor } from "./SubworkflowEditor";

/**
 * SubworkflowEditor lets the author pick a child workflow and map onto ITS declared inputs. These
 * pin: the picker excludes the workflow being edited (no obvious self-loop); selecting a target
 * sets config.workflowId and clears the stale input mapping; the selected child's declared inputs
 * render (so they're mappable); a workflow with no inputs says so.
 */
vi.mock("@/hooks/use-workflows", () => ({
  useWorkflows: () => ({
    data: [
      { id: "child-1", name: "Review PR" },
      { id: "child-empty", name: "Ping" },
      { id: "self", name: "Me" },
    ],
    isLoading: false,
  }),
  useWorkflow: (id: string | null) => ({
    data:
      id === "child-1" ? { definition: { inputs: [{ name: "repo", schema: { type: "string" }, required: true }] } }
      : id === "child-empty" ? { definition: { inputs: [] } }
      : null,
    isLoading: false,
  }),
}));

function renderEditor(config: Record<string, unknown>, handlers: { onConfigChange?: ReturnType<typeof vi.fn>; onInputsChange?: ReturnType<typeof vi.fn> } = {}) {
  const onConfigChange = handlers.onConfigChange ?? vi.fn();
  const onInputsChange = handlers.onInputsChange ?? vi.fn();
  render(
    <SubworkflowEditor
      config={config}
      inputs={{ inputs: {} }}
      onConfigChange={onConfigChange}
      onInputsChange={onInputsChange}
      suggestions={[]}
      currentWorkflowId="self"
    />,
  );
  return { onConfigChange, onInputsChange };
}

describe("SubworkflowEditor", () => {
  it("lists other workflows but not the one being edited", () => {
    renderEditor({});

    expect(screen.getByRole("option", { name: "Review PR" })).toBeTruthy();
    expect(screen.queryByRole("option", { name: "Me" })).toBeNull();
  });

  it("selecting a workflow sets workflowId and clears the stale input mapping", () => {
    const { onConfigChange, onInputsChange } = renderEditor({});

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "child-1" } });

    expect(onConfigChange).toHaveBeenCalledWith({ workflowId: "child-1" });
    expect(onInputsChange).toHaveBeenCalledWith({ inputs: {} });
  });

  it("renders the selected child's declared inputs for mapping", () => {
    renderEditor({ workflowId: "child-1" });

    // SchemaForm humanizes the input name for the label ("repo" → "Repo").
    expect(screen.getByText(/repo/i)).toBeTruthy();
  });

  it("says so when the selected workflow declares no inputs", () => {
    renderEditor({ workflowId: "child-empty" });

    expect(screen.getByText(/declares no inputs/i)).toBeTruthy();
  });
});
