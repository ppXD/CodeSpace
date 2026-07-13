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
  // The primary Model picker (CredentialedModelSelector) flattens the team's credentialed models; one OpenAI
  // model (codex-cli-compatible) and one Anthropic model (NOT — codex supports OpenAI/Custom only).
  useCredentialedModels: () => ({ isLoading: false, data: [
    { rowId: "m1", modelId: "gpt-5-codex", credentialId: "c1", credentialName: "Team key", provider: "OpenAI", available: true },
    { rowId: "m2", modelId: "claude-sonnet-4", credentialId: "c2", credentialName: "Anthropic key", provider: "Anthropic", available: true },
  ] }),
}));
vi.mock("@/hooks/use-chat", () => ({
  useConversations: () => ({ isLoading: false, data: [{ id: "conv1", kind: "Channel", slug: "review", name: "Review" }] }),
}));
vi.mock("./selectors/RepositoryWorkspacePicker", () => ({
  RepositoryWorkspacePicker: ({ repositoryId }: { repositoryId: string }) => <div data-testid="repo-selector">{repositoryId}</div>,
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
    expect(screen.getByText("Harness")).toBeInTheDocument();   // the Harness section header + picker
  });

  it("starts in Agent mode when a persona is bound, showing the persona picker", () => {
    render(<AgentCodeInspector {...baseProps} config={{ harness: "codex-cli", agentDefinitionId: "p1" }} />);
    expect(screen.getByText("Agent persona")).toBeInTheDocument();   // Agent mode → the persona-picker row is shown
  });

  it("starts in Inline mode with no persona, showing the instructions field (no persona picker)", () => {
    render(<AgentCodeInspector {...baseProps} config={{ harness: "codex-cli" }} />);
    expect(screen.getByLabelText("What should the agent do?")).toBeInTheDocument();
    expect(screen.queryByText("Agent persona")).not.toBeInTheDocument();
  });

  it("clears the bound persona when switching to Configure inline", () => {
    const onConfigChange = vi.fn();
    render(<AgentCodeInspector {...baseProps} onConfigChange={onConfigChange} config={{ harness: "codex-cli", agentDefinitionId: "p1" }} />);

    fireEvent.click(screen.getByRole("tab", { name: "Configure inline" }));

    // agentDefinitionId is blanked → deleted from the saved config (harness preserved).
    expect(onConfigChange).toHaveBeenCalledWith({ harness: "codex-cli" });
  });

  it("offers the chosen harness's models as datalist suggestions for the manual model field", () => {
    const { container } = render(<AgentCodeInspector {...baseProps} config={{ harness: "codex-cli" }} />);
    const option = container.querySelector("#agentcode-model-hints option");
    expect(option?.getAttribute("value")).toBe("gpt-5-codex");
  });

  it("lists only harness-compatible credentialed models, and picking one sets model+credential in one choice", () => {
    const onConfigChange = vi.fn();
    render(<AgentCodeInspector {...baseProps} onConfigChange={onConfigChange} config={{ harness: "codex-cli" }} />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Pick a model…" }));
    expect(screen.queryByRole("option", { name: /claude-sonnet-4/ })).toBeNull();   // Anthropic model excluded (codex = OpenAI/Custom)
    fireEvent.mouseDown(screen.getByRole("option", { name: /gpt-5-codex/ }));

    // one pick stores the credentialed-model row id; the loose model/credential stay absent
    expect(onConfigChange).toHaveBeenCalledWith({ harness: "codex-cli", modelCredentialModelId: "m1" });
  });

  it("clears pre-existing manual model + credential when a credentialed model is picked (they'd be discarded)", () => {
    const onConfigChange = vi.fn();
    render(<AgentCodeInspector {...baseProps} onConfigChange={onConfigChange} config={{ harness: "codex-cli", model: "gpt-4o", modelCredentialId: "c1" }} />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Pick a model…" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: /gpt-5-codex/ }));

    expect(onConfigChange).toHaveBeenCalledWith({ harness: "codex-cli", modelCredentialModelId: "m1" });
  });

  it("clears the picked credentialed model when a manual model id is typed (so the manual value takes effect)", () => {
    const onConfigChange = vi.fn();
    render(<AgentCodeInspector {...baseProps} onConfigChange={onConfigChange} config={{ harness: "codex-cli", modelCredentialModelId: "m1" }} />);

    fireEvent.change(screen.getByPlaceholderText("Leave blank for the harness default"), { target: { value: "o1-preview" } });

    expect(onConfigChange).toHaveBeenCalledWith({ harness: "codex-cli", model: "o1-preview" });
  });

  it("shows the approval-conversation picker, reflecting the saved value", () => {
    render(<AgentCodeInspector {...baseProps} config={{ harness: "codex-cli", approvalConversationId: "conv1" }} />);
    expect(screen.getByText("#review")).toBeInTheDocument();   // the saved conversation, shown as a chip
  });

  it("patches approvalConversationId into config when a conversation is picked", () => {
    const onConfigChange = vi.fn();
    render(<AgentCodeInspector {...baseProps} onConfigChange={onConfigChange} config={{ harness: "codex-cli" }} />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Pick a conversation…" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: "#review" }));

    expect(onConfigChange).toHaveBeenCalledWith({ harness: "codex-cli", approvalConversationId: "conv1" });
  });
});
