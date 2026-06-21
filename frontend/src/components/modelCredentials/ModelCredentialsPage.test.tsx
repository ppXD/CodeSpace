import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { ModelCredentialSummary } from "@/api/modelCredentials";

import { ModelCredentialsPage } from "./ModelCredentialsPage";

const mocks = vi.hoisted(() => ({
  rows: [] as ModelCredentialSummary[],
  models: [] as { id: string; modelId: string; enabled: boolean }[],
  addMutate: vi.fn(),
  revokeMutate: vi.fn(),
  confirmFn: vi.fn(),
}));

vi.mock("@/hooks/use-model-credentials", () => ({
  useModelCredentials: () => ({ data: mocks.rows, isLoading: false, error: null }),
  useAddModelCredential: () => ({ mutate: mocks.addMutate, isPending: false }),
  useUpdateModelCredential: () => ({ mutate: vi.fn(), isPending: false }),
  useRevokeModelCredential: () => ({ mutate: mocks.revokeMutate, isPending: false }),
  useCredentialedModelList: () => ({ data: mocks.models, isLoading: false, error: null }),
  useRefreshCredentialedModels: () => ({ mutate: vi.fn(), isPending: false }),
  useSaveCredentialedModels: () => ({ mutate: vi.fn(), isPending: false }),
}));
vi.mock("@/components/dialog", () => ({ useConfirm: () => mocks.confirmFn }));

const cred: ModelCredentialSummary = {
  id: "mc1", teamId: "t1", provider: "Anthropic", displayName: "Team Anthropic",
  keyHint: "····a1b2", baseUrl: null, status: "Active", createdDate: "2026-06-11T00:00:00Z",
};

describe("ModelCredentialsPage", () => {
  beforeEach(() => {
    mocks.rows = [];
    mocks.models = [];
    mocks.addMutate.mockReset();
    mocks.revokeMutate.mockReset();
    mocks.confirmFn.mockReset().mockResolvedValue(true);
  });

  it("shows only the masked key hint for a configured credential, never the secret", () => {
    mocks.rows = [cred];
    render(<ModelCredentialsPage />);

    expect(screen.getByText("Team Anthropic")).toBeInTheDocument();
    expect(screen.getByText("····a1b2")).toBeInTheDocument();   // masked tail, not a full key
    expect(screen.getByText("Active")).toBeInTheDocument();
  });

  it("shows an empty state when the team has no credentials", () => {
    render(<ModelCredentialsPage />);
    expect(screen.getByText("No model credentials yet")).toBeInTheDocument();
  });

  it("opens the add modal with a provider picker and a key field", () => {
    render(<ModelCredentialsPage />);

    fireEvent.click(screen.getByRole("button", { name: "Add credential" }));

    expect(screen.getByText("Add model credential")).toBeInTheDocument();
    expect(screen.getByRole("combobox")).toBeInTheDocument();        // provider picker
    expect(screen.getByText("Provider")).toBeInTheDocument();
    expect(screen.getByText("API key")).toBeInTheDocument();          // Anthropic default → key field rendered
  });

  it("seeds models inline on the add-credential modal via the row editor", () => {
    render(<ModelCredentialsPage />);
    fireEvent.click(screen.getByRole("button", { name: "Add credential" }));

    expect(screen.getByText("Models · optional")).toBeInTheDocument();
    expect(screen.getByPlaceholderText("model-id")).toBeInTheDocument();   // one row to start
    expect(screen.getByPlaceholderText("Display name")).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "Add model" }));    // appends a blank row
    expect(screen.getAllByPlaceholderText("model-id")).toHaveLength(2);
  });

  it("drops the secret field for a keyless provider", () => {
    render(<ModelCredentialsPage />);
    fireEvent.click(screen.getByRole("button", { name: "Add credential" }));

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "Ollama" } });

    expect(screen.queryByText("API key")).not.toBeInTheDocument();   // keyless → no secret field
    expect(screen.getByText("Base URL")).toBeInTheDocument();        // base url is the required field
  });

  it("shows the model count on the card and opens the models manager", () => {
    mocks.rows = [cred];
    mocks.models = [{ id: "m1", modelId: "claude-sonnet-4-5", enabled: true }, { id: "m2", modelId: "claude-opus-4-8", enabled: true }];
    render(<ModelCredentialsPage />);

    fireEvent.click(screen.getByText("2 models"));

    expect(screen.getByText("Team Anthropic · Models")).toBeInTheDocument();
    expect(screen.getByDisplayValue("claude-sonnet-4-5")).toBeInTheDocument();   // loaded into an editable row
    expect(screen.getByDisplayValue("claude-opus-4-8")).toBeInTheDocument();
  });

  it("confirms before revoking and revokes by id", async () => {
    mocks.rows = [cred];
    render(<ModelCredentialsPage />);

    fireEvent.click(screen.getByRole("button", { name: "Revoke" }));

    await waitFor(() => expect(mocks.confirmFn).toHaveBeenCalled());
    await waitFor(() => expect(mocks.revokeMutate).toHaveBeenCalledWith("mc1"));
  });
});
