import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { ModelCredentialSummary } from "@/api/modelCredentials";

import { ModelCredentialModelsModal } from "./ModelCredentialModelsModal";

const mocks = vi.hoisted(() => ({
  models: [] as { id: string; modelId: string; displayName?: string | null; enabled: boolean; isDefault?: boolean; inputUsdPerMillion?: number | null; outputUsdPerMillion?: number | null }[],
  saveMutate: vi.fn(),
  refreshMutate: vi.fn(),
  setDefaultMutate: vi.fn(),
  setPriceMutate: vi.fn(),
}));

vi.mock("@/hooks/use-model-credentials", async () => {
  // parsePrice is the real thing — its "blank is unpriced, never $0" rule is what the modal's half-price guard
  // depends on, so stubbing it would test nothing.
  const actual = await vi.importActual<typeof import("@/hooks/use-model-credentials")>("@/hooks/use-model-credentials");

  return {
    parsePrice: actual.parsePrice,
    useCredentialedModelList: () => ({ data: mocks.models, isLoading: false, error: null }),
    useRefreshCredentialedModels: () => ({ mutate: mocks.refreshMutate, isPending: false }),
    useSaveCredentialedModels: () => ({ mutate: mocks.saveMutate, isPending: false }),
    useSetDefaultCredentialedModel: () => ({ mutate: mocks.setDefaultMutate, isPending: false }),
    useSetCredentialedModelPrice: () => ({ mutate: mocks.setPriceMutate, isPending: false }),
  };
});

const cred: ModelCredentialSummary = {
  id: "mc1", teamId: "t1", provider: "Anthropic", displayName: "Team Anthropic",
  keyHint: "····a1b2", keyUnreadable: false, baseUrl: null, status: "Active", createdDate: "2026-06-11T00:00:00Z",
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
    mocks.setDefaultMutate.mockReset();
    mocks.setPriceMutate.mockReset();
  });

  it("marks a model as the default when its star is clicked", () => {
    mocks.models = [
      { id: "m1", modelId: "metis-coder", enabled: true, isDefault: false },
      { id: "m2", modelId: "metis-coder-max", enabled: true, isDefault: true },
    ];
    renderModal();

    const stars = screen.getAllByTitle(/default model for auto runs/i);
    expect(stars).toHaveLength(2);                                          // one per existing row
    fireEvent.click(stars[0]);                                             // star the non-default row (metis-coder)
    expect(mocks.setDefaultMutate).toHaveBeenCalledWith("m1", expect.anything());
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
      { id: "m1", modelId: "claude-sonnet-4-5", displayName: "", isDefault: undefined, inputUsdPerMillion: "", outputUsdPerMillion: "" },
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

  // ── D1: the two price fields — what makes a run's cost cap enforceable for this model ──

  it("loads a model's stored price into the two money fields", () => {
    mocks.models = [{ id: "m1", modelId: "gpt-5.4-codex", enabled: true, inputUsdPerMillion: 2, outputUsdPerMillion: 10 }];
    renderModal();

    expect(screen.getByPlaceholderText("$/M in")).toHaveValue("2");
    expect(screen.getByPlaceholderText("$/M out")).toHaveValue("10");
  });

  it("hints that a model with neither price set is unpriced", () => {
    mocks.models = [{ id: "m1", modelId: "gpt-5.4-codex", enabled: true }];
    renderModal();

    expect(screen.getByPlaceholderText("$/M in")).toHaveClass("is-unpriced");
    expect(screen.getByPlaceholderText("$/M in").getAttribute("title")).toMatch(/cost cap cannot spend/i);
  });

  it("commits a COMPLETE price pair on blur", () => {
    mocks.models = [{ id: "m1", modelId: "gpt-5.4-codex", enabled: true }];
    renderModal();

    fireEvent.change(screen.getByPlaceholderText("$/M in"), { target: { value: "2" } });
    fireEvent.change(screen.getByPlaceholderText("$/M out"), { target: { value: "10.5" } });
    fireEvent.blur(screen.getByPlaceholderText("$/M out"));

    expect(mocks.setPriceMutate).toHaveBeenCalledWith(
      { modelRowId: "m1", input: { inputUsdPerMillion: 2, outputUsdPerMillion: 10.5 } },
      expect.anything(),
    );
  });

  it("does NOT commit HALF a price", () => {
    // Half a price prices nothing: storing it would make the row look priced while a capped run still refuses to
    // spend on it. The backend rejects it too — this just avoids a pointless round-trip mid-edit.
    mocks.models = [{ id: "m1", modelId: "gpt-5.4-codex", enabled: true }];
    renderModal();

    fireEvent.change(screen.getByPlaceholderText("$/M in"), { target: { value: "2" } });
    fireEvent.blur(screen.getByPlaceholderText("$/M in"));

    expect(mocks.setPriceMutate).not.toHaveBeenCalled();
  });

  it("clears a price when BOTH fields are emptied", () => {
    mocks.models = [{ id: "m1", modelId: "gpt-5.4-codex", enabled: true, inputUsdPerMillion: 2, outputUsdPerMillion: 10 }];
    renderModal();

    fireEvent.change(screen.getByPlaceholderText("$/M in"), { target: { value: "" } });
    fireEvent.change(screen.getByPlaceholderText("$/M out"), { target: { value: "" } });
    fireEvent.blur(screen.getByPlaceholderText("$/M out"));

    expect(mocks.setPriceMutate).toHaveBeenCalledWith(
      { modelRowId: "m1", input: { inputUsdPerMillion: null, outputUsdPerMillion: null } },
      expect.anything(),
    );
  });

  it("a BRAND-NEW row's price rides along with Save, not with a blur", () => {
    // A row with no backend id has nothing to PUT against yet — its price is carried on the add.
    renderModal();
    fireEvent.click(screen.getByRole("button", { name: "Add model" }));

    const ids = screen.getAllByPlaceholderText("model-id");
    fireEvent.change(ids[1], { target: { value: "gpt-5.4-codex" } });

    const inputs = screen.getAllByPlaceholderText("$/M in");
    fireEvent.change(inputs[1], { target: { value: "2" } });
    fireEvent.blur(inputs[1]);

    expect(mocks.setPriceMutate).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Save" }));
    const [arg] = mocks.saveMutate.mock.calls[0];
    expect(arg.rows[1]).toEqual({ modelId: "gpt-5.4-codex", displayName: "", inputUsdPerMillion: "2" });
  });

  it("refreshes the model list from the provider", () => {
    renderModal();
    fireEvent.click(screen.getByText(/Refresh from provider/));
    expect(mocks.refreshMutate).toHaveBeenCalled();
  });
});
