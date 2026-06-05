import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AddWorkflowModal } from "./AddWorkflowModal";
import type { WorkflowTemplate } from "@/hooks/use-workflow-templates";

const TEMPLATES: WorkflowTemplate[] = [
  { id: "ai-code-review", name: "AI Code Review", description: "Auto-comment a PR.", definition: { schemaVersion: 1, nodes: [], edges: [] }, activations: [], enabled: true },
  { id: "ai-pr-review-gate", name: "AI PR Review + Approval Gate", description: "AI review then an approval gate.", definition: { schemaVersion: 1, nodes: [], edges: [] }, activations: [], enabled: false },
];

function renderModal(over: Partial<Parameters<typeof AddWorkflowModal>[0]> = {}) {
  const props = { templates: TEMPLATES, pending: false, onBlank: vi.fn(), onTemplate: vi.fn(), onClose: vi.fn(), ...over };
  render(<AddWorkflowModal {...props} />);
  return props;
}

describe("AddWorkflowModal", () => {
  it("opens on the Blank vs Template choice — templates not shown yet", () => {
    renderModal();
    expect(screen.getByRole("button", { name: /Blank/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Template/ })).toBeInTheDocument();
    expect(screen.queryByText("AI PR Review + Approval Gate")).toBeNull();
  });

  it("Blank calls onBlank", () => {
    const { onBlank } = renderModal();
    fireEvent.click(screen.getByRole("button", { name: /Blank/ }));
    expect(onBlank).toHaveBeenCalledTimes(1);
  });

  it("Template reveals the template cards; picking one calls onTemplate with that template", () => {
    const { onTemplate } = renderModal();
    fireEvent.click(screen.getByRole("button", { name: /Template/ }));

    expect(screen.getByText("AI Code Review")).toBeInTheDocument();
    expect(screen.getByText("AI PR Review + Approval Gate")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /AI PR Review/ }));
    expect(onTemplate).toHaveBeenCalledWith(expect.objectContaining({ id: "ai-pr-review-gate" }));
  });

  it("Back returns from the template list to the choice", () => {
    renderModal();
    fireEvent.click(screen.getByRole("button", { name: /Template/ }));
    fireEvent.click(screen.getByRole("button", { name: /Back/ }));

    expect(screen.getByRole("button", { name: /Blank/ })).toBeInTheDocument();
    expect(screen.queryByText("AI PR Review + Approval Gate")).toBeNull();
  });

  it("Escape closes", () => {
    const { onClose } = renderModal();
    fireEvent.keyDown(window, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
