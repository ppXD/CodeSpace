import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { CredentialedModelMultiSelector, CredentialedModelSelector } from "./CredentialedModelSelector";

/**
 * Both variants render the shared SearchSelect combobox; the saved value is the model ROW id (the
 * (credential, model) handle the backend pool resolves via ResolveByRowIdAsync) — never the bare model id.
 * The rich combobox behaviour is covered in SearchSelect.test.tsx; here we prove the option mapping (rowId +
 * credential meta) and that the emitted value is the rowId. Hook mocked: useCredentialedModels.
 */
vi.mock("@/hooks/use-model-credentials", () => ({
  useCredentialedModels: () => ({
    isLoading: false,
    data: [
      { rowId: "r1", modelId: "claude-opus-4-8", credentialId: "c1", credentialName: "Team Anthropic", provider: "Anthropic", tier: "Frontier", available: true },
      { rowId: "r2", modelId: "gpt-5", credentialId: "c2", credentialName: "Team OpenAI", provider: "OpenAI", tier: "Strong", available: true },
      { rowId: "r3", modelId: "local-x", credentialId: "c3", credentialName: "Self host", provider: "Custom", tier: null, available: false },
    ],
  }),
}));

describe("CredentialedModelSelector (single)", () => {
  it("emits the ROW id (not the model id) on pick", () => {
    const onChange = vi.fn();
    render(<CredentialedModelSelector value="" onChange={onChange} />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Pick a model…" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: /claude-opus-4-8/ }));
    expect(onChange).toHaveBeenCalledWith("r1");   // the rowId, NOT "claude-opus-4-8"
  });

  it("shows the credential + offline meta in the option", () => {
    render(<CredentialedModelSelector value="" onChange={() => {}} />);
    fireEvent.focus(screen.getByRole("textbox", { name: "Pick a model…" }));
    expect(screen.getByRole("option", { name: /local-x/ }).textContent).toContain("Self host (Custom) — offline");
  });

  it("renders a saved model as a chip and clears to empty on remove", () => {
    const onChange = vi.fn();
    render(<CredentialedModelSelector value="r1" onChange={onChange} />);

    expect(screen.getByText("claude-opus-4-8")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove claude-opus-4-8" }));
    expect(onChange).toHaveBeenCalledWith("");   // single cleared → empty string
  });
});

describe("CredentialedModelMultiSelector", () => {
  it("renders chips + count hint and adds by ROW id", () => {
    const onChange = vi.fn();
    render(<CredentialedModelMultiSelector value={["r1"]} onChange={onChange} />);

    expect(screen.getByText("claude-opus-4-8")).toBeInTheDocument();
    expect(screen.getByText("1 selected — dispatched agents must use one of these.")).toBeInTheDocument();

    fireEvent.focus(screen.getByRole("textbox", { name: "Search models…" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: /gpt-5/ }));
    expect(onChange).toHaveBeenCalledWith(["r1", "r2"]);   // rowIds
  });

  it("treats an empty selection as 'any allowed'", () => {
    render(<CredentialedModelMultiSelector value={[]} onChange={() => {}} />);
    expect(screen.getByText("None selected — any of the team's models may be used.")).toBeInTheDocument();
  });
});
