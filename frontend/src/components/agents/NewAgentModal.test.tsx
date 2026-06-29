import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PackDetail, PackSummary } from "@/api/packs";

const h = vi.hoisted(() => ({
  usePacks: vi.fn(),
  usePack: vi.fn(),
  useInstantiate: vi.fn(),
  mutateAsync: vi.fn(),
  reset: vi.fn(),
}));

vi.mock("@/hooks/use-packs", () => ({ usePacks: h.usePacks, usePack: h.usePack }));
vi.mock("@/hooks/use-agents", () => ({ useInstantiateAgentFromStore: h.useInstantiate }));

import { NewAgentModal } from "./NewAgentModal";

function pack(over: Partial<PackSummary>): PackSummary {
  return { id: over.id ?? "p", kind: "Github", name: over.name ?? "Pack", url: null, reference: null, lastSyncedSha: null, lastSyncedDate: null, agentCount: over.agentCount ?? 0, skillCount: over.skillCount ?? 0 };
}

function setup(opts: { packs?: Partial<{ data: PackSummary[]; isLoading: boolean; isError: boolean }>; detail?: PackDetail; pending?: boolean; isError?: boolean } = {}) {
  h.usePacks.mockReturnValue({ data: opts.packs?.data ?? [], isLoading: opts.packs?.isLoading ?? false, isError: opts.packs?.isError ?? false });
  h.usePack.mockReturnValue({ data: opts.detail ?? null, isLoading: false });
  h.mutateAsync.mockResolvedValue({ id: "new-agent-1" });
  h.useInstantiate.mockReturnValue({ mutateAsync: h.mutateAsync, isPending: opts.pending ?? false, isError: opts.isError ?? false, reset: h.reset });
  const props = { onCustom: vi.fn(), onCreated: vi.fn(), onClose: vi.fn() };
  render(<NewAgentModal {...props} />);
  return props;
}

describe("NewAgentModal", () => {
  beforeEach(() => vi.clearAllMocks());

  it("offers Custom and From Library on-ramps; Custom calls onCustom", () => {
    const { onCustom } = setup();
    expect(screen.getByRole("button", { name: /From Library/ })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: /Custom/ }));
    expect(onCustom).toHaveBeenCalledTimes(1);
  });

  it("shows an empty-Library message when no pack has agents", () => {
    setup({ packs: { data: [pack({ id: "skills-only", agentCount: 0, skillCount: 2 })] } });
    fireEvent.click(screen.getByRole("button", { name: /From Library/ }));
    expect(screen.getByText(/No agents in your Library yet/)).toBeInTheDocument();
  });

  it("surfaces a load failure distinctly from an empty Library", () => {
    setup({ packs: { isError: true } });
    fireEvent.click(screen.getByRole("button", { name: /From Library/ }));
    expect(screen.getByText(/Couldn't load your Library/)).toBeInTheDocument();
  });

  it("drills pack → agent and instantiates the picked agent, handing the new id back", async () => {
    const detail: PackDetail = {
      pack: pack({ id: "p1", name: "Pack One", agentCount: 1 }),
      artifacts: [{ kind: "Agent", id: "a1", slug: "reviewer", name: "Reviewer", description: null, sourcePath: null }],
    };
    const { onCreated } = setup({ packs: { data: [pack({ id: "p1", name: "Pack One", agentCount: 1 })] }, detail });

    fireEvent.click(screen.getByRole("button", { name: /From Library/ }));
    fireEvent.click(screen.getByRole("button", { name: /Pack One/ }));
    fireEvent.click(screen.getByRole("button", { name: /Reviewer/ }));

    await waitFor(() => expect(h.mutateAsync).toHaveBeenCalledWith("a1"));
    await waitFor(() => expect(onCreated).toHaveBeenCalledWith("new-agent-1"));
  });

  it("disables agent picks while an instantiate is in flight (no double-submit)", () => {
    const detail: PackDetail = {
      pack: pack({ id: "p1", name: "Pack One", agentCount: 1 }),
      artifacts: [{ kind: "Agent", id: "a1", slug: "reviewer", name: "Reviewer", description: null, sourcePath: null }],
    };
    setup({ packs: { data: [pack({ id: "p1", name: "Pack One", agentCount: 1 })] }, detail, pending: true });

    fireEvent.click(screen.getByRole("button", { name: /From Library/ }));
    fireEvent.click(screen.getByRole("button", { name: /Pack One/ }));
    expect(screen.getByRole("button", { name: /Reviewer/ })).toBeDisabled();
  });
});
