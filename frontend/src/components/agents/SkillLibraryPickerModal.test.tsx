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
vi.mock("@/hooks/use-skills", () => ({ useInstantiateSkillFromStore: h.useInstantiate }));

import { SkillLibraryPickerModal } from "./SkillLibraryPickerModal";

function pack(over: Partial<PackSummary>): PackSummary {
  return { id: over.id ?? "p", kind: "Github", name: over.name ?? "Pack", url: null, reference: null, lastSyncedSha: null, lastSyncedDate: null, agentCount: over.agentCount ?? 0, skillCount: over.skillCount ?? 0 };
}

function setup(opts: { packs?: PackSummary[]; detail?: PackDetail; pending?: boolean; pickedSourceIds?: Set<string> } = {}) {
  h.usePacks.mockReturnValue({ data: opts.packs ?? [], isLoading: false, isError: false });
  h.usePack.mockReturnValue({ data: opts.detail ?? null, isLoading: false });
  h.mutateAsync.mockResolvedValue({ id: "ws1" });
  h.useInstantiate.mockReturnValue({ mutateAsync: h.mutateAsync, isPending: opts.pending ?? false, isError: false, reset: h.reset });
  const onPicked = vi.fn();
  const onClose = vi.fn();
  render(<SkillLibraryPickerModal pickedSourceIds={opts.pickedSourceIds ?? new Set()} onPicked={onPicked} onClose={onClose} />);
  return { onPicked, onClose };
}

describe("SkillLibraryPickerModal", () => {
  beforeEach(() => vi.clearAllMocks());

  it("shows an empty message when no pack has skills", () => {
    setup({ packs: [pack({ id: "agents-only", agentCount: 2, skillCount: 0 })] });
    expect(screen.getByText(/No skills in your Library yet/)).toBeInTheDocument();
  });

  it("drills pack → skill, instantiates the picked store skill, and hands back the working-copy id", async () => {
    const detail: PackDetail = {
      pack: pack({ id: "p1", name: "Superpowers", skillCount: 1 }),
      artifacts: [{ kind: "Skill", id: "store-tdd", slug: "tdd", name: "TDD", description: null, sourcePath: null }],
    };
    const { onPicked } = setup({ packs: [pack({ id: "p1", name: "Superpowers", skillCount: 1 })], detail });

    fireEvent.click(screen.getByRole("button", { name: /Superpowers/ }));
    fireEvent.click(screen.getByRole("button", { name: /TDD/ }));

    await waitFor(() => expect(h.mutateAsync).toHaveBeenCalledWith("store-tdd"));
    await waitFor(() => expect(onPicked).toHaveBeenCalledWith("ws1", "store-tdd"));
  });

  it("disables skill picks while an instantiate is in flight", () => {
    const detail: PackDetail = {
      pack: pack({ id: "p1", name: "Superpowers", skillCount: 1 }),
      artifacts: [{ kind: "Skill", id: "store-tdd", slug: "tdd", name: "TDD", description: null, sourcePath: null }],
    };
    setup({ packs: [pack({ id: "p1", name: "Superpowers", skillCount: 1 })], detail, pending: true });

    fireEvent.click(screen.getByRole("button", { name: /Superpowers/ }));
    expect(screen.getByRole("button", { name: /TDD/ })).toBeDisabled();
  });

  it("disables a store skill whose source is already added (dedupe by source — never bind the same skill twice)", () => {
    const detail: PackDetail = {
      pack: pack({ id: "p1", name: "Superpowers", skillCount: 1 }),
      artifacts: [{ kind: "Skill", id: "store-tdd", slug: "tdd", name: "TDD", description: null, sourcePath: null }],
    };
    setup({ packs: [pack({ id: "p1", name: "Superpowers", skillCount: 1 })], detail, pickedSourceIds: new Set(["store-tdd"]) });

    fireEvent.click(screen.getByRole("button", { name: /Superpowers/ }));
    const tdd = screen.getByRole("button", { name: /TDD/ });
    expect(tdd).toBeDisabled();
    fireEvent.click(tdd);
    expect(h.mutateAsync).not.toHaveBeenCalled();
  });
});
