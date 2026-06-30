import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PackArtifactSummary, PackSummary, PagedArtifacts } from "@/api/packs";

const h = vi.hoisted(() => ({
  usePacks: vi.fn(),
  useListPackArtifacts: vi.fn(),
  useSyncPack: vi.fn(),
  mutate: vi.fn(),
}));

// Debounce-to-identity so a search keystroke applies to the query key synchronously (no fake timers needed).
vi.mock("@/hooks/use-debounced", () => ({ useDebounced: (v: unknown) => v }));
vi.mock("@/hooks/use-packs", () => ({ usePacks: h.usePacks, useListPackArtifacts: h.useListPackArtifacts, useSyncPack: h.useSyncPack }));

import { LibraryPage } from "./LibraryPage";

function pack(over: Partial<PackSummary>): PackSummary {
  return { id: over.id ?? "p1", kind: over.kind ?? "Github", name: over.name ?? "Pack", url: null, reference: null, lastSyncedSha: null, lastSyncedDate: null, agentCount: over.agentCount ?? 0, skillCount: over.skillCount ?? 0 };
}

function artifact(over: Partial<PackArtifactSummary>): PackArtifactSummary {
  return { kind: over.kind ?? "Agent", id: over.id ?? "a1", slug: over.slug ?? "a1", name: over.name ?? "Agent One", description: over.description ?? null, sourcePath: over.sourcePath ?? null };
}

function page(over: Partial<PagedArtifacts>): PagedArtifacts {
  return { items: over.items ?? [], total: over.total ?? 0, page: over.page ?? 0, pageCount: over.pageCount ?? 1 };
}

/** Args of the most recent useListPackArtifacts call: [packId, kind, search, page, pageSize]. */
function lastQuery() {
  return h.useListPackArtifacts.mock.calls.at(-1) as [string, string, string, number, number];
}

function setup(opts: { packs: PackSummary[]; artifacts?: PagedArtifacts; isFetching?: boolean; isError?: boolean; error?: { message: string } }) {
  h.usePacks.mockReturnValue({ data: opts.packs, isLoading: false, error: null });
  h.useListPackArtifacts.mockReturnValue({ data: opts.artifacts, isFetching: opts.isFetching ?? false, isError: opts.isError ?? false, error: opts.error ?? null });
  h.useSyncPack.mockReturnValue({ mutate: h.mutate, isPending: false, error: null });
  render(<LibraryPage />);
}

describe("LibraryPage detail pane (server-paged)", () => {
  beforeEach(() => vi.clearAllMocks());

  it("renders the active kind's page and takes the tab counts from the pack summary, not the page", () => {
    setup({
      packs: [pack({ id: "p1", name: "Superpowers", agentCount: 20, skillCount: 5 })],
      artifacts: page({ items: [artifact({ id: "a1", name: "Backend Architect" })], total: 20, page: 0, pageCount: 2 }),
    });

    // Tab badges reflect the pack's kind TOTALS (20 / 5), independent of the single-item page that loaded.
    expect(screen.getByRole("tab", { name: /Agents/ })).toHaveTextContent("20");
    expect(screen.getByRole("tab", { name: /Skills/ })).toHaveTextContent("5");
    expect(screen.getByText("Backend Architect")).toBeInTheDocument();

    // Opens on the Agent kind, page 0, no search.
    const [packId, kind, search, p] = lastQuery();
    expect([packId, kind, search, p]).toEqual(["p1", "Agent", "", 0]);

    // The pager reflects the SERVER total, not the page length.
    expect(screen.getByText("1–12 of 20")).toBeInTheDocument();
  });

  it("switches to the Skill kind and resets to page 0 on a tab click", () => {
    setup({
      packs: [pack({ id: "p1", agentCount: 20, skillCount: 5 })],
      artifacts: page({ items: [artifact({ id: "a1" })], total: 20, page: 0, pageCount: 2 }),
    });

    fireEvent.click(screen.getByRole("button", { name: /Next page/ }));
    expect(lastQuery()[3]).toBe(1);   // advanced to page 1

    fireEvent.click(screen.getByRole("tab", { name: /Skills/ }));
    const [, kind, , p] = lastQuery();
    expect(kind).toBe("Skill");
    expect(p).toBe(0);                // tab switch reset the page
  });

  it("filters by the typed search and resets to page 0", () => {
    setup({
      packs: [pack({ id: "p1", agentCount: 20, skillCount: 0 })],
      artifacts: page({ items: [artifact({ id: "a1" })], total: 20, page: 0, pageCount: 2 }),
    });

    fireEvent.click(screen.getByRole("button", { name: /Next page/ }));
    expect(lastQuery()[3]).toBe(1);

    fireEvent.change(screen.getByLabelText(/Search agents/), { target: { value: "deploy" } });
    const [, , search, p] = lastQuery();
    expect(search).toBe("deploy");
    expect(p).toBe(0);                // a new search starts from the first page
  });

  it("clears the search when switching tabs (no carried filter on the new kind)", () => {
    setup({
      packs: [pack({ id: "p1", agentCount: 20, skillCount: 5 })],
      artifacts: page({ items: [artifact({ id: "a1" })], total: 20, page: 0, pageCount: 2 }),
    });

    fireEvent.change(screen.getByLabelText(/Search agents/), { target: { value: "deploy" } });
    fireEvent.click(screen.getByRole("tab", { name: /Skills/ }));

    expect(lastQuery()[1]).toBe("Skill");
    expect(lastQuery()[2]).toBe("");                                    // the term did NOT carry over
    expect(screen.getByLabelText(/Search skills/)).toHaveValue("");
  });

  it("does not flash the other kind's rows during a tab switch (cross-kind placeholder gated to Loading)", () => {
    // Active tab is Agents, but keepPreviousData is holding a SKILL page (mid-switch). It must NOT render under Agents.
    setup({
      packs: [pack({ id: "p1", agentCount: 20, skillCount: 5 })],
      artifacts: page({ items: [artifact({ kind: "Skill", id: "s1", name: "Git Rebase" })], total: 5, page: 0, pageCount: 1 }),
      isFetching: true,
    });

    expect(screen.queryByText("Git Rebase")).not.toBeInTheDocument();   // the wrong-kind row is suppressed
    expect(screen.getByText("Loading…")).toBeInTheDocument();
  });

  it("keeps the loaded list on a failed background refetch, but shows an error on a failed first load", () => {
    setup({
      packs: [pack({ id: "p1", agentCount: 20, skillCount: 0 })],
      artifacts: page({ items: [artifact({ id: "a1", name: "Backend Architect" })], total: 20, page: 0, pageCount: 2 }),
      isError: true,
      error: { message: "boom" },
    });
    expect(screen.getByText("Backend Architect")).toBeInTheDocument();   // cached page survives a background error
    expect(screen.queryByText(/Couldn't load/)).not.toBeInTheDocument();

    vi.clearAllMocks();
    setup({ packs: [pack({ id: "p1", agentCount: 20, skillCount: 0 })], artifacts: undefined, isError: true, error: { message: "boom" } });
    expect(screen.getByText(/Couldn't load/)).toBeInTheDocument();       // first load (no data) DOES surface the error
  });

  it("disables the pager while a fetch is in flight (so a rapid click can't navigate off a stale page)", () => {
    setup({
      packs: [pack({ id: "p1", agentCount: 20, skillCount: 0 })],
      artifacts: page({ items: [artifact({ id: "a1" })], total: 20, page: 0, pageCount: 2 }),
      isFetching: true,
    });
    expect(screen.getByRole("button", { name: /Next page/ })).toBeDisabled();
  });

  it("shows a no-match message when a search returns nothing", () => {
    setup({
      packs: [pack({ id: "p1", agentCount: 20, skillCount: 0 })],
      artifacts: page({ items: [], total: 0, page: 0, pageCount: 1 }),
    });

    fireEvent.change(screen.getByLabelText(/Search agents/), { target: { value: "zzz" } });
    expect(screen.getByText(/No agents match/)).toBeInTheDocument();
  });

  it("shows the no-artifacts state for an empty pack", () => {
    setup({ packs: [pack({ id: "p1", agentCount: 0, skillCount: 0 })], artifacts: page({}) });
    expect(screen.getByText(/No active artifacts/)).toBeInTheDocument();
  });
});
