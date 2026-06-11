import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AgentCodeInspector } from "./AgentCodeInspector";

// The real harness / agent / credential pickers fetch over the network; mock their hooks so the
// selectors render synchronously from fixed data. The repository selector + the {{}} picker are
// stubbed as simple controls (they carry their own heavy hooks / contenteditable, out of scope here).
vi.mock("@/hooks/use-agents", () => ({
  useHarnesses: () => ({ isLoading: false, data: [{ kind: "codex-cli", version: "1", models: ["gpt-5-codex"], supportedProviders: ["OpenAI", "Custom"] }] }),
  useAgentDefinitions: () => ({ isLoading: false, data: [{ id: "p1", slug: "reviewer", name: "Reviewer" }] }),
}));
vi.mock("@/hooks/use-model-credentials", () => ({
  useModelCredentials: () => ({ isLoading: false, data: [{ id: "c1", provider: "OpenAI", displayName: "Team key", status: "Active" }] }),
}));
vi.mock("./selectors/ProjectRepositorySelector", () => ({
  ProjectRepositorySelector: ({ value }: { value: string }) => <div data-testid="repo-selector">{value}</div>,
}));
vi.mock("./VariablePickerInput", () => ({
  VariablePickerInput: ({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder?: string }) => (
    <textarea aria-label={placeholder} value={value} onChange={(e) => onChange(e.target.value)} />
  ),
}));

const baseProps = {
  inputs: {},
  onConfigChange: vi.fn(),
  onInputsChange: vi.fn(),
  suggestions: [],
};

describe("AgentCodeInspector", () => {
  it("always shows the harness picker, in either mode", () => {
    render(<AgentCodeInspector {...baseProps} config={{ harness: "codex-cli" }} />);
    expect(screen.getByLabelText("Harness")).toBeInTheDocument();
  });

  it("starts in Agent mode when a persona is bound, showing the persona picker", () => {
    render(<AgentCodeInspector {...baseProps} config={{ harness: "codex-cli", agentDefinitionId: "p1" }} />);
    expect(screen.getByLabelText("Agent persona")).toBeInTheDocument();
    expect((screen.getByLabelText("Agent persona") as HTMLSelectElement).value).toBe("p1");
  });

  it("starts in Inline mode with no persona, showing the instructions field (no persona picker)", () => {
    render(<AgentCodeInspector {...baseProps} config={{ harness: "codex-cli" }} />);
    expect(screen.getByLabelText("What should the agent do?")).toBeInTheDocument();
    expect(screen.queryByLabelText("Agent persona")).not.toBeInTheDocument();
  });

  it("clears the bound persona when switching to Configure inline", () => {
    const onConfigChange = vi.fn();
    render(<AgentCodeInspector {...baseProps} onConfigChange={onConfigChange} config={{ harness: "codex-cli", agentDefinitionId: "p1" }} />);

    fireEvent.click(screen.getByRole("tab", { name: "Configure inline" }));

    // agentDefinitionId is blanked → deleted from the saved config (harness preserved).
    expect(onConfigChange).toHaveBeenCalledWith({ harness: "codex-cli" });
  });

  it("offers the chosen harness's models as datalist suggestions for the model field", () => {
    const { container } = render(<AgentCodeInspector {...baseProps} config={{ harness: "codex-cli" }} />);
    const option = container.querySelector("#agentcode-model-hints option");
    expect(option?.getAttribute("value")).toBe("gpt-5-codex");
  });
});
