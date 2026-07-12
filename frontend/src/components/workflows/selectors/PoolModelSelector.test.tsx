import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { PoolModelSelector } from "./PoolModelSelector";

// Mock only the flattened-pool hook; keep the module's other real exports.
const mockUse = vi.fn();
vi.mock("@/hooks/use-model-credentials", async (orig) => {
  const actual = await orig<typeof import("@/hooks/use-model-credentials")>();
  return { ...actual, useCredentialedModels: () => mockUse() };
});

const OPTS = [
  { rowId: "r1", modelId: "claude-sonnet-4-5", credentialId: "c1", credentialName: "Anthropic key", provider: "Anthropic", available: true },
  { rowId: "r2", modelId: "gpt-4o", credentialId: "c2", credentialName: "OpenAI key", provider: "OpenAI", available: true },
  { rowId: "r3", modelId: "gpt-4o", credentialId: "c3", credentialName: "OpenAI key 2", provider: "OpenAI", available: true }, // dup modelId under same provider
];

describe("PoolModelSelector", () => {
  it("lists only the sibling provider's models (deduped) and stores the model-id on pick", () => {
    mockUse.mockReturnValue({ data: OPTS, isLoading: false });
    const onChange = vi.fn();
    render(<PoolModelSelector provider="OpenAI" value="" onChange={onChange} />);

    fireEvent.focus(screen.getByLabelText(/Pick a model/));
    expect(screen.queryByRole("option", { name: /claude-sonnet-4-5/ })).toBeNull();   // Anthropic model excluded
    expect(screen.getAllByRole("option", { name: /gpt-4o/ })).toHaveLength(1);          // two credentials → one row

    fireEvent.mouseDown(screen.getByRole("option", { name: /gpt-4o/ }));
    expect(onChange).toHaveBeenCalledWith("gpt-4o");   // the MODEL-ID string, not a row id
  });

  it("defaults an unset provider to Anthropic (matching the backend fallback)", () => {
    mockUse.mockReturnValue({ data: OPTS, isLoading: false });
    render(<PoolModelSelector provider={undefined} value="" onChange={vi.fn()} />);

    fireEvent.focus(screen.getByLabelText(/Pick a model/));
    expect(screen.getByRole("option", { name: /claude-sonnet-4-5/ })).toBeTruthy();
    expect(screen.queryByRole("option", { name: /gpt-4o/ })).toBeNull();
  });

  it("prompts for Expression when the provider is a dynamic {{ref}}", () => {
    mockUse.mockReturnValue({ data: OPTS, isLoading: false });
    render(<PoolModelSelector provider="{{trigger.provider}}" value="" onChange={vi.fn()} />);

    expect(screen.getByText(/switch to Expression/)).toBeTruthy();
  });

  it("keeps a pre-existing value the pool no longer lists visible (chip shows the id, not 'Unavailable') and hints about it", () => {
    mockUse.mockReturnValue({ data: OPTS, isLoading: false });
    render(<PoolModelSelector provider="OpenAI" value="o1-preview" onChange={vi.fn()} />);

    expect(screen.getByText("o1-preview")).toBeTruthy();          // chip resolves to the real id...
    expect(screen.queryByText("Unavailable")).toBeNull();          // ...not the blank fallback
    expect(screen.getByText(/isn't in OpenAI's enabled pool/)).toBeTruthy();   // and the hint flags it
  });

  it("hints to add a model when the chosen provider has none", () => {
    mockUse.mockReturnValue({ data: [], isLoading: false });
    render(<PoolModelSelector provider="Custom" value="" onChange={vi.fn()} />);

    expect(screen.getByText(/No enabled models under Custom/)).toBeTruthy();
  });
});
