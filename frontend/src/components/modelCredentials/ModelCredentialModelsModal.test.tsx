import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { ModelCredentialSummary } from "@/api/modelCredentials";

import { ModelCredentialModelsModal } from "./ModelCredentialModelsModal";

const mocks = vi.hoisted(() => ({
  models: [] as { id: string; modelId: string; displayName?: string | null; enabled: boolean }[],
  saveMutate: vi.fn(),
  refreshMutate: vi.fn(),
}));

vi.mock("@/hooks/use-model-credentials", () => ({
  useCredentialedModelList: () => ({ data: mocks.models, isLoading: false, error: null }),
  useRefreshCredentialedModels: () => ({ mutate: mocks.refreshMutate, isPending: false }),
  useSaveCredentialedModels: () => ({ mutate: mocks.saveMutate, isPending: false }),
}));

const cred: ModelCredentialSummary = {
  id: "mc1", teamId: "t1", provider: "Anthropic", displayName: "Team Anthropic",
  keyHint: "····a1b2", baseUrl: null, status: "Active", createdDate: "2026-06-11T00:00:00Z",
};

function renderModal() {
  const onClose = vi.fn();
  render(<ModelCredentialModelsModal credential={cred} onClose={onClose} />);
  return { onClose };
}

describe("ModelCredentialModelsModal", () => {
  beforeEach(() => {
    mocks.models = [{ id: "m1", modelId: "claude-sonnet-4-5", enabled: true }];
    mocks.saveMutate.mockReset();
    mocks.refreshMutate.mockReset();
  });

  it("loads the credential's models into editable rows", () => {
    renderModal();
    expect(screen.getByText("1 model")).toBeInTheDocument();
    expect(screen.getByDisplayValue("claude-sonnet-4-5")).toBeInTheDocument();
  });

  it("appends a blank row with Add model", () => {
    renderModal();
    expect(screen.getAllByPlaceholderText("model-id")).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Add model" }));
    expect(screen.getAllByPlaceholderText("model-id")).toHaveLength(2);
  });

  it("Save reconciles the edited rows against the originals", () => {
    renderModal();
    fireEvent.click(screen.getByRole("button", { name: "Add model" }));
    const ids = screen.getAllByPlaceholderText("model-id");
    fireEvent.change(ids[1], { target: { value: "claude-opus-4-8" } });

    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    expect(mocks.saveMutate).toHaveBeenCalledTimes(1);
    const [arg] = mocks.saveMutate.mock.calls[0];
    expect(arg.original).toEqual(mocks.models);
    expect(arg.rows).toEqual([
      { id: "m1", modelId: "claude-sonnet-4-5", displayName: "" },
      { modelId: "claude-opus-4-8", displayName: "" },
    ]);
  });

  it("removing a row then saving drops it from the reconciliation set", () => {
    renderModal();
    fireEvent.click(screen.getByTitle("Remove model"));
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    const [arg] = mocks.saveMutate.mock.calls[0];
    expect(arg.rows).toEqual([]);
  });

  it("refreshes the model list from the provider", () => {
    renderModal();
    fireEvent.click(screen.getByText(/Refresh from provider/));
    expect(mocks.refreshMutate).toHaveBeenCalled();
  });
});
