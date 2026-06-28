import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PackArtifactSummary, PackSummary, PagedArtifacts } from "@/api/packs";

const h = vi.hoisted(() => ({
  usePacks: vi.fn(),
  useListPackArtifacts: vi.fn(),
  useInstantiate: vi.fn(),
  mutateAsync: vi.fn(),
}));

vi.mock("@/hooks/use-debounced", () => ({ useDebounced: (v: unknown) => v }));
vi.mock("@/hooks/use-packs", () => ({ usePacks: h.usePacks, useListPackArtifacts: h.useListPackArtifacts }));
vi.mock("@/hooks/use-skills", () => ({ useInstantiateSkillFromStore: h.useInstantiate }));

import { SkillBindingDropdown } from "./SkillBindingDropdown";

function pack(over: Partial<PackSummary>): PackSummary {
  return { id: over.id ?? "p", kind: "Github", name: over.name ?? "Pack", url: null, reference: null, lastSyncedSha: null, lastSyncedDate: null, agentCount: over.agentCount ?? 0, skillCount: over.skillCount ?? 0 };
}

function skill(over: Partial<PackArtifactSummary>): PackArtifactSummary {
  return { kind: "Skill", id: over.id ?? "s1", slug: over.slug ?? "tdd", name: over.name ?? "TDD", description: null, sourcePath: null };
}

function page(over: Partial<PagedArtifacts>): PagedArtifacts {
  return { items: over.items ?? [], total: over.total ?? 0, page: over.page ?? 0, pageCount: over.pageCount ?? 1 };
}

function lastQuery() {
  return h.useListPackArtifacts.mock.calls.at(-1) as [string, string, string, number, number];
}

/** Controlled harness so onChange updates `selected` and the dropdown's check state reflects the new binding.
 *  labelFor is id-passthrough so a bound chip ("ws1") never collides with a panel skill name ("TDD") in queries. */
function Harness() {
  const [sel, setSel] = useState<string[]>([]);
  return (
    <>
      <SkillBindingDropdown selected={sel} onChange={setSel} labelFor={(id) => id} />
      <div data-testid="sel">{sel.join(",")}</div>
    </>
  );
}

function setup(opts: { packs?: PackSummary[]; skills?: PagedArtifacts } = {}) {
  h.usePacks.mockReturnValue({ data: opts.packs ?? [], isLoading: false, isError: false });
  h.useListPackArtifacts.mockReturnValue({ data: opts.skills, isFetching: false, isError: false });
  h.mutateAsync.mockResolvedValue({ id: "ws1" });
  h.useInstantiate.mockReturnValue({ mutateAsync: h.mutateAsync, isPending: false, isError: false, variables: undefined, reset: vi.fn() });
  render(<Harness />);
}

const sel = () => screen.getByTestId("sel").textContent;

describe("SkillBindingDropdown", () => {
  beforeEach(() => vi.clearAllMocks());

  it("opens the left-right panel and queries the first pack's skills", () => {
    setup({ packs: [pack({ id: "p1", name: "Superpowers", skillCount: 2 })], skills: page({ items: [skill({ id: "store-tdd", name: "TDD" })], total: 2 }) });
    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ }));

    expect(screen.getByText("TDD")).toBeInTheDocument();
    expect(lastQuery()).toEqual(["p1", "Skill", "", 0, 8]);
  });

  it("instantiates a working copy and binds it on first pick, then checks the row", async () => {
    setup({ packs: [pack({ id: "p1", skillCount: 1 })], skills: page({ items: [skill({ id: "store-tdd", name: "TDD" })], total: 1 }) });
    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ }));
    fireEvent.click(screen.getByRole("button", { name: /TDD/ }));

    await waitFor(() => expect(h.mutateAsync).toHaveBeenCalledWith("store-tdd"));
    await waitFor(() => expect(sel()).toBe("ws1"));
    expect(screen.getByRole("button", { name: /TDD/ })).toHaveAttribute("aria-pressed", "true");
  });

  it("toggles a bound skill off (unbind) without re-instantiating, then rebinds the SAME copy on re-pick", async () => {
    setup({ packs: [pack({ id: "p1", skillCount: 1 })], skills: page({ items: [skill({ id: "store-tdd", name: "TDD" })], total: 1 }) });
    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ }));

    fireEvent.click(screen.getByRole("button", { name: /TDD/ }));        // bind
    await waitFor(() => expect(sel()).toBe("ws1"));

    fireEvent.click(screen.getByRole("button", { name: /TDD/ }));        // unbind
    await waitFor(() => expect(sel()).toBe(""));

    fireEvent.click(screen.getByRole("button", { name: /TDD/ }));        // rebind reuses ws1
    await waitFor(() => expect(sel()).toBe("ws1"));

    expect(h.mutateAsync).toHaveBeenCalledTimes(1);                      // never minted a duplicate copy
  });

  it("remembers a skill it minted after the dropdown is closed and reopened (no duplicate copy)", async () => {
    setup({ packs: [pack({ id: "p1", skillCount: 1 })], skills: page({ items: [skill({ id: "store-tdd", name: "TDD" })], total: 1 }) });

    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ }));
    fireEvent.click(screen.getByRole("button", { name: /TDD/ }));        // mint + bind
    await waitFor(() => expect(sel()).toBe("ws1"));

    fireEvent.mouseDown(document.body);                                  // close (panel unmounts)
    expect(screen.queryByText("TDD")).not.toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ })); // reopen
    // bySource survived (it lives on the always-mounted dropdown), so the row is still CHECKED and a re-pick unbinds
    // — it does NOT mint a second copy.
    expect(screen.getByRole("button", { name: /TDD/ })).toHaveAttribute("aria-pressed", "true");
    fireEvent.click(screen.getByRole("button", { name: /TDD/ }));
    await waitFor(() => expect(sel()).toBe(""));
    expect(h.mutateAsync).toHaveBeenCalledTimes(1);                      // never re-minted across the close/reopen
  });

  it("loads another pack's skills when a rail item is selected", () => {
    setup({ packs: [pack({ id: "p1", name: "Pack One", skillCount: 1 }), pack({ id: "p2", name: "Pack Two", skillCount: 1 })], skills: page({ items: [skill({})], total: 1 }) });
    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ }));
    expect(lastQuery()[0]).toBe("p1");

    fireEvent.click(screen.getByRole("option", { name: /Pack Two/ }));
    expect(lastQuery()[0]).toBe("p2");
  });

  it("closes the panel on an outside click", () => {
    setup({ packs: [pack({ id: "p1", skillCount: 1 })], skills: page({ items: [skill({ name: "TDD" })], total: 1 }) });
    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ }));
    expect(screen.getByText("TDD")).toBeInTheDocument();

    fireEvent.mouseDown(document.body);
    expect(screen.queryByText("TDD")).not.toBeInTheDocument();
  });

  it("shows an empty-Library message when no pack has skills", () => {
    setup({ packs: [pack({ id: "agents-only", agentCount: 3, skillCount: 0 })] });
    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ }));
    expect(screen.getByText(/No skills in your Library yet/)).toBeInTheDocument();
  });

  it("renders bound skills as removable labels inside the field (placeholder when empty)", async () => {
    setup({ packs: [pack({ id: "p1", skillCount: 1 })], skills: page({ items: [skill({ id: "store-tdd", name: "TDD" })], total: 1 }) });

    expect(screen.getByText(/Add skills/)).toBeInTheDocument();   // placeholder while nothing is bound

    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ }));
    fireEvent.click(screen.getByRole("button", { name: /TDD/ }));
    await waitFor(() => expect(sel()).toBe("ws1"));

    fireEvent.mouseDown(document.body);                           // close the panel; the label stays in the field
    expect(screen.queryByText("TDD")).not.toBeInTheDocument();    // (panel content gone)

    const remove = screen.getByRole("button", { name: /Remove ws1/ });
    fireEvent.click(remove);                                      // removing the label unbinds — and must NOT reopen the panel
    await waitFor(() => expect(sel()).toBe(""));
    expect(screen.queryByText("TDD")).not.toBeInTheDocument();
  });

  it("keyboard: Enter on the field opens the picker, but a key on a chip's remove button does not toggle it", async () => {
    setup({ packs: [pack({ id: "p1", skillCount: 1 })], skills: page({ items: [skill({ id: "store-tdd", name: "TDD" })], total: 1 }) });

    fireEvent.click(screen.getByRole("button", { name: /Bound skills/ }));
    fireEvent.click(screen.getByRole("button", { name: /TDD/ }));
    await waitFor(() => expect(sel()).toBe("ws1"));
    fireEvent.mouseDown(document.body);                           // close
    expect(screen.queryByText("TDD")).not.toBeInTheDocument();

    // A key on the nested remove button must NOT bubble up and toggle the picker open.
    fireEvent.keyDown(screen.getByRole("button", { name: /Remove ws1/ }), { key: "Enter" });
    expect(screen.queryByText("TDD")).not.toBeInTheDocument();

    // Enter on the field itself DOES open it (target === the field).
    fireEvent.keyDown(screen.getByRole("button", { name: /Bound skills/ }), { key: "Enter" });
    expect(screen.getByText("TDD")).toBeInTheDocument();
  });
});
