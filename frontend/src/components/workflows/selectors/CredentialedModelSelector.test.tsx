import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { CredentialedModelMultiSelector, CredentialedModelSelector } from "./CredentialedModelSelector";

/**
 * The credentialed-model pickers save the model's ROW id (the (credential, model) handle the backend pool
 * resolves via ResolveByRowIdAsync) — never the bare model id or the credential id. Hook mocked:
 * useCredentialedModels. Two credentials exposing the same model name are distinct rows, so rowId is the
 * only unambiguous handle — the tests assert it is what gets emitted.
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
  it("lists models by name+credential and emits the chosen ROW id (not the model id)", () => {
    const onChange = vi.fn();
    render(<CredentialedModelSelector value="" onChange={onChange} />);

    expect(screen.getByRole("option", { name: "claude-opus-4-8 · Team Anthropic (Anthropic)" })).toBeInTheDocument();
    expect(screen.getByRole("option", { name: "Pick a model…" })).toBeInTheDocument();

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "r1" } });
    expect(onChange).toHaveBeenCalledWith("r1");   // the rowId, NOT "claude-opus-4-8"
  });

  it("flags an offline model but keeps it selectable", () => {
    render(<CredentialedModelSelector value="" onChange={() => {}} />);
    expect(screen.getByRole("option", { name: "local-x · Self host (Custom) — offline" })).toBeInTheDocument();
  });

  it("keeps a saved-but-missing row visible and flagged, never silently blanked", () => {
    render(<CredentialedModelSelector value="gone" onChange={() => {}} />);

    expect(screen.getByRole("option", { name: "Saved model — unavailable" })).toBeInTheDocument();
    expect((screen.getByRole("combobox") as HTMLSelectElement).value).toBe("gone");
  });
});

describe("CredentialedModelMultiSelector", () => {
  it("renders selected rows as tags with a count hint", () => {
    render(<CredentialedModelMultiSelector value={["r1"]} onChange={() => {}} />);

    expect(screen.getByText("claude-opus-4-8 · Team Anthropic")).toBeInTheDocument();
    expect(screen.getByText("1 selected — dispatched agents must use one of these.")).toBeInTheDocument();
  });

  it("treats an empty selection as 'any allowed'", () => {
    render(<CredentialedModelMultiSelector value={[]} onChange={() => {}} />);
    expect(screen.getByText("None selected — any of the team's models may be used.")).toBeInTheDocument();
  });

  it("removes a selected row by ROW id", () => {
    const onChange = vi.fn();
    render(<CredentialedModelMultiSelector value={["r1", "r2"]} onChange={onChange} />);

    fireEvent.click(screen.getByRole("button", { name: "Remove claude-opus-4-8 · Team Anthropic" }));
    expect(onChange).toHaveBeenCalledWith(["r2"]);
  });

  it("adds an unselected row by its ROW id from the search dropdown", () => {
    const onChange = vi.fn();
    render(<CredentialedModelMultiSelector value={[]} onChange={onChange} />);

    fireEvent.focus(screen.getByRole("textbox", { name: "Search models" }));
    fireEvent.mouseDown(screen.getByRole("option", { name: "gpt-5 · Team OpenAI (OpenAI)" }));
    expect(onChange).toHaveBeenCalledWith(["r2"]);   // the rowId
  });

  it("keeps a saved row that's no longer in the pool as an 'Unavailable model' tag, still removable", () => {
    const onChange = vi.fn();
    render(<CredentialedModelMultiSelector value={["r1", "gone"]} onChange={onChange} />);

    expect(screen.getByText("Unavailable model")).toBeInTheDocument();   // the stale rowId isn't silently dropped
    fireEvent.click(screen.getByRole("button", { name: "Remove Unavailable model" }));
    expect(onChange).toHaveBeenCalledWith(["r1"]);   // only the removed id goes; the known one stays
  });
});
