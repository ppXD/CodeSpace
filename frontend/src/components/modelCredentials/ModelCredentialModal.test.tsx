import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { ModelCredentialSummary } from "@/api/modelCredentials";

import { ModelCredentialModal } from "./ModelCredentialModal";

const mocks = vi.hoisted(() => ({ updateMutate: vi.fn() }));

vi.mock("@/hooks/use-model-credentials", async () => {
  const actual = await vi.importActual<typeof import("@/hooks/use-model-credentials")>("@/hooks/use-model-credentials");

  return {
    completePrice: actual.completePrice,
    contextWindowFieldIssue: actual.contextWindowFieldIssue,
    parseContextWindow: actual.parseContextWindow,
    useAddModelCredential: () => ({ mutate: vi.fn(), isPending: false }),
    useUpdateModelCredential: () => ({ mutate: mocks.updateMutate, isPending: false }),
  };
});

const gateway: ModelCredentialSummary = {
  id: "mc1", teamId: "t1", provider: "Custom", displayName: "Gateway",
  keyHint: "····a1b2", keyUnreadable: false, baseUrl: "https://***:***@gw.example/v1?api-key=***", status: "Active", createdDate: "2026-06-11T00:00:00Z",
};

const baseUrlInput = () => screen.getByPlaceholderText("https://your-gateway/v1");
const saveButton = () => screen.getByRole("button", { name: "Save" });

describe("ModelCredentialModal — editing the endpoint", () => {
  beforeEach(() => mocks.updateMutate.mockReset());

  it("sends the masked base URL back exactly as shown when only the name changes, so the stored credentials are kept", () => {
    render(<ModelCredentialModal editing={gateway} onClose={vi.fn()} />);

    fireEvent.change(screen.getByDisplayValue("Gateway"), { target: { value: "Renamed" } });
    fireEvent.click(saveButton());

    expect(mocks.updateMutate).toHaveBeenCalledWith({ id: "mc1", input: { displayName: "Renamed", apiKey: null, baseUrl: gateway.baseUrl } }, expect.anything());
    expect(screen.getByText(/Credentials in the URL show as/)).toBeInTheDocument();
  });

  it("asks for the key again before saving a move to another host", () => {
    render(<ModelCredentialModal editing={gateway} onClose={vi.fn()} />);

    fireEvent.change(baseUrlInput(), { target: { value: "https://attacker.example/v1" } });

    expect(saveButton()).toBeDisabled();
    expect(screen.getByPlaceholderText("Required for the new endpoint")).toBeInTheDocument();
    expect(screen.getByText(/The endpoint changed — enter the key for it/)).toBeInTheDocument();

    fireEvent.change(screen.getByPlaceholderText("Required for the new endpoint"), { target: { value: "sk-new-gateway-key" } });
    fireEvent.click(saveButton());

    expect(mocks.updateMutate).toHaveBeenCalledWith({ id: "mc1", input: { displayName: "Gateway", apiKey: "sk-new-gateway-key", baseUrl: "https://attacker.example/v1" } }, expect.anything());
  });

  it("keeps the key optional for a path-only change on the same endpoint", () => {
    render(<ModelCredentialModal editing={{ ...gateway, baseUrl: "https://gw.example/v1" }} onClose={vi.fn()} />);

    fireEvent.change(baseUrlInput(), { target: { value: "https://gw.example/v2" } });

    expect(saveButton()).toBeEnabled();
    expect(screen.queryByText(/The endpoint changed/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Credentials in the URL show as/)).not.toBeInTheDocument();
  });

  it("never asks a keyless credential for a key", () => {
    render(<ModelCredentialModal editing={{ ...gateway, provider: "Ollama", keyHint: null, baseUrl: "http://localhost:11434" }} onClose={vi.fn()} />);

    fireEvent.change(screen.getByPlaceholderText("http://localhost:11434"), { target: { value: "http://ollama.internal:11434" } });

    expect(saveButton()).toBeEnabled();
  });
});
