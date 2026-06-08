import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AddWorkflowModal } from "./AddWorkflowModal";
import type { WorkflowTemplate } from "@/hooks/use-workflow-templates";

const TEMPLATES: WorkflowTemplate[] = [
  { id: "ai-code-review", name: "AI Code Review", description: "Auto-comment a PR.", definition: { schemaVersion: 1, nodes: [], edges: [] }, activations: [], enabled: true },
  { id: "ai-pr-review-gate", name: "AI PR Review + Approval Gate", description: "AI review then an approval gate.", definition: { schemaVersion: 1, nodes: [], edges: [] }, activations: [], enabled: false },
];

function renderModal(over: Partial<Parameters<typeof AddWorkflowModal>[0]> = {}) {
  const props = { templates: TEMPLATES, pending: false, onBlank: vi.fn(), onTask: vi.fn(), onTemplate: vi.fn(), onClose: vi.fn(), ...over };
  render(<AddWorkflowModal {...props} />);
  return props;
}

describe("AddWorkflowModal", () => {
  it("opens on the three on-ramps — neither templates nor the task field shown yet", () => {
    renderModal();
    expect(screen.getByRole("button", { name: /Describe a task/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Blank/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Template/ })).toBeInTheDocument();
    expect(screen.queryByText("AI PR Review + Approval Gate")).toBeNull();
    expect(screen.queryByRole("textbox")).toBeNull();
  });

  it("Blank calls onBlank", () => {
    const { onBlank } = renderModal();
    fireEvent.click(screen.getByRole("button", { name: /Blank/ }));
    expect(onBlank).toHaveBeenCalledTimes(1);
  });

  it("Describe a task reveals the textarea; Create is disabled until text is entered, then calls onTask with the task", () => {
    const { onTask } = renderModal();
    fireEvent.click(screen.getByRole("button", { name: /Describe a task/ }));

    const field = screen.getByRole("textbox");
    expect(field).toBeInTheDocument();

    const create = screen.getByRole("button", { name: /Create agent/ });
    expect(create).toBeDisabled();

    fireEvent.change(field, { target: { value: "Triage incoming bugs" } });
    expect(create).not.toBeDisabled();

    fireEvent.click(create);
    expect(onTask).toHaveBeenCalledWith("Triage incoming bugs");
  });

  it("Create stays disabled for whitespace-only input", () => {
    const { onTask } = renderModal();
    fireEvent.click(screen.getByRole("button", { name: /Describe a task/ }));
    fireEvent.change(screen.getByRole("textbox"), { target: { value: "   " } });

    expect(screen.getByRole("button", { name: /Create agent/ })).toBeDisabled();
    expect(onTask).not.toHaveBeenCalled();
  });

  it("Back returns from the task step to the choice", () => {
    renderModal();
    fireEvent.click(screen.getByRole("button", { name: /Describe a task/ }));
    fireEvent.click(screen.getByRole("button", { name: /Back/ }));

    expect(screen.getByRole("button", { name: /Blank/ })).toBeInTheDocument();
    expect(screen.queryByRole("textbox")).toBeNull();
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
