import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { SchemaForm } from "./SchemaForm";

// The poolModel selector reads a SIBLING `provider` from the values bag SchemaForm threads through the dispatch
// chain (same mechanism as actorUser's repositoryId). Mock only the flattened-pool hook.
const mockUse = vi.fn();
vi.mock("@/hooks/use-model-credentials", async (orig) => {
  const actual = await orig<typeof import("@/hooks/use-model-credentials")>();
  return { ...actual, useCredentialedModels: () => mockUse() };
});

const OPTS = [
  { rowId: "r2", modelId: "gpt-4o", credentialId: "c2", credentialName: "OpenAI key", provider: "OpenAI", available: true },
];

const schema = {
  type: "object",
  properties: {
    provider: { type: "string", enum: ["Anthropic", "OpenAI", "Custom"] },
    model: { type: "string", "x-selector": "poolModel" },
  },
};

describe("SchemaForm x-selector: poolModel (sibling provider threading)", () => {
  it("scopes the model picker to the sibling provider value", () => {
    mockUse.mockReturnValue({ data: OPTS, isLoading: false });
    render(<SchemaForm schema={schema} value={{ provider: "OpenAI" }} onChange={vi.fn()} />);

    fireEvent.focus(screen.getByLabelText(/Pick a model/));
    expect(screen.getByRole("option", { name: /gpt-4o/ })).toBeTruthy();   // sibling provider=OpenAI threaded → its model listed
  });

  it("shows no models when the sibling provider has none (proves the scoping, not a static list)", () => {
    mockUse.mockReturnValue({ data: OPTS, isLoading: false });
    render(<SchemaForm schema={schema} value={{ provider: "Anthropic" }} onChange={vi.fn()} />);

    expect(screen.getByText(/No enabled models under Anthropic/)).toBeTruthy();
  });
});
