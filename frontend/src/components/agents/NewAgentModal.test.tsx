import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PackArtifactSummary, PackSummary, PagedArtifacts } from "@/api/packs";

const h = vi.hoisted(() => ({
  usePacks: vi.fn(),
  useListPackArtifacts: vi.fn(),
  useInstantiate: vi.fn(),
  mutateAsync: vi.fn(),
  reset: vi.fn(),
}));

vi.mock("@/hooks/use-debounced", () => ({ useDebounced: (v: unknown) => v }));
vi.mock("@/hooks/use-packs", () => ({ usePacks: h.usePacks, useListPackArtifacts: h.useListPackArtifacts }));
vi.mock("@/hooks/use-agents", () => ({ useInstantiateAgentFromStore: h.useInstantiate }));

import { NewAgentModal } from "./NewAgentModal";

function pack(over: Partial<PackSummary>): PackSummary {
  return { id: over.id ?? "p", kind: "Github", name: over.name ?? "Pack", url: null, reference: null, lastSyncedSha: null, lastSyncedDate: null, agentCount: over.agentCount ?? 0, skillCount: over.skillCount ?? 0 };
}

function agent(over: Partial<PackArtifactSummary>): PackArtifactSummary {
  return { kind: "Agent", id: over.id ?? "a1", slug: over.slug ?? "reviewer", name: over.name ?? "Reviewer", description: over.description ?? null, sourcePath: null };
}

function page(over: Partial<PagedArtifacts>): PagedArtifacts {
  return { items: over.items ?? [], total: over.total ?? 0, page: over.page ?? 0, pageCount: over.pageCount ?? 1 };
}

/** Args of the most recent useListPackArtifacts call: [packId, kind, search, page, pageSize]. */
function lastQuery() {
  return h.useListPackArtifacts.mock.calls.at(-1) as [string, string, string, number, number];
}

function setup(opts: { packs?: Partial<{ data: PackSummary[]; isLoading: boolean; isError: boolean }>; agents?: PagedArtifacts; pending?: boolean; instantiateError?: boolean; isFetching?: boolean } = {}) {
  h.usePacks.mockReturnValue({ data: opts.packs?.data ?? [], isLoading: opts.packs?.isLoading ?? false, isError: opts.packs?.isError ?? false });
  h.useListPackArtifacts.mockReturnValue({ data: opts.agents, isFetching: opts.isFetching ?? false, isError: false });
  h.mutateAsync.mockResolvedValue({ id: "new-agent-1" });
  h.useInstantiate.mockReturnValue({ mutateAsync: h.mutateAsync, isPending: opts.pending ?? false, isError: opts.instantiateError ?? false, reset: h.reset });
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

  it("auto-selects the first pack and instantiates a picked agent, handing the new id back", async () => {
    const { onCreated } = setup({
      packs: { data: [pack({ id: "p1", name: "Pack One", agentCount: 1 })] },
      agents: page({ items: [agent({ id: "a1", name: "Reviewer" })], total: 1 }),
    });

    fireEvent.click(screen.getByRole("button", { name: /From Library/ }));
    // No pack drill: the first pack is auto-selected, so its agents show immediately.
    expect(lastQuery()).toEqual(["p1", "Agent", "", 0, 10]);

    fireEvent.click(screen.getByRole("button", { name: /Reviewer/ }));
    await waitFor(() => expect(h.mutateAsync).toHaveBeenCalledWith("a1"));
    await waitFor(() => expect(onCreated).toHaveBeenCalledWith("new-agent-1"));
  });

  it("loads a different pack's agents when another rail item is selected", () => {
    setup({
      packs: { data: [pack({ id: "p1", name: "Pack One", agentCount: 1 }), pack({ id: "p2", name: "Pack Two", agentCount: 1 })] },
      agents: page({ items: [agent({ id: "a1", name: "Reviewer" })], total: 1 }),
    });

    fireEvent.click(screen.getByRole("button", { name: /From Library/ }));
    expect(lastQuery()[0]).toBe("p1");                         // first pack auto-selected

    fireEvent.click(screen.getByRole("option", { name: /Pack Two/ }));
    expect(lastQuery()[0]).toBe("p2");                         // rail switch re-queries the new pack
  });

  it("clears a prior add-failure banner when switching packs in the rail", () => {
    setup({
      packs: { data: [pack({ id: "p1", name: "Pack One", agentCount: 1 }), pack({ id: "p2", name: "Pack Two", agentCount: 1 })] },
      agents: page({ items: [agent({ id: "a1", name: "Reviewer" })], total: 1 }),
      instantiateError: true,
    });

    fireEvent.click(screen.getByRole("button", { name: /From Library/ }));
    fireEvent.click(screen.getByRole("option", { name: /Pack Two/ }));
    expect(h.reset).toHaveBeenCalled();                        // the stale failure banner doesn't follow onto Pack Two
  });

  it("disables agent picks while an instantiate is in flight (no double-submit)", () => {
    setup({
      packs: { data: [pack({ id: "p1", name: "Pack One", agentCount: 1 })] },
      agents: page({ items: [agent({ id: "a1", name: "Reviewer" })], total: 1 }),
      pending: true,
    });

    fireEvent.click(screen.getByRole("button", { name: /From Library/ }));
    expect(screen.getByRole("button", { name: /Reviewer/ })).toBeDisabled();
  });
});
