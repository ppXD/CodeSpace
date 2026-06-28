import { describe, expect, it } from "vitest";

import type { PackSummary } from "@/api/packs";

import { countLabel, paginate, resolveDetailTab, resolveSelectedPackId, sourceLabel } from "./libraryView";

const pack = (over: Partial<PackSummary>): PackSummary => ({
  id: "p1", kind: "Github", name: "agents", url: null, reference: null,
  lastSyncedSha: null, lastSyncedDate: null, agentCount: 0, skillCount: 0, ...over,
});

describe("countLabel", () => {
  it("pluralizes each count and joins with a middot", () => {
    expect(countLabel(2, 3)).toBe("2 agents · 3 skills");
    expect(countLabel(1, 1)).toBe("1 agent · 1 skill");
  });

  it("omits a zero side", () => {
    expect(countLabel(5, 0)).toBe("5 agents");
    expect(countLabel(0, 4)).toBe("4 skills");
  });

  it("reports an empty pack", () => {
    expect(countLabel(0, 0)).toBe("empty");
  });
});

describe("resolveSelectedPackId", () => {
  const packs = [pack({ id: "a" }), pack({ id: "b" }), pack({ id: "c" })];

  it("keeps an explicit pick that still exists", () => {
    expect(resolveSelectedPackId("b", packs)).toBe("b");
  });

  it("defaults to the first pack when nothing is picked", () => {
    expect(resolveSelectedPackId(null, packs)).toBe("a");
  });

  it("falls back to the first pack when the pick no longer exists", () => {
    expect(resolveSelectedPackId("gone", packs)).toBe("a");
  });

  it("returns null when there are no packs", () => {
    expect(resolveSelectedPackId("a", [])).toBeNull();
    expect(resolveSelectedPackId(null, [])).toBeNull();
  });
});

describe("sourceLabel", () => {
  it("reduces a github URL to owner/repo", () => {
    expect(sourceLabel(pack({ url: "https://github.com/contains-studio/agents" }))).toBe("contains-studio/agents");
  });

  it("strips a trailing .git and slashes", () => {
    expect(sourceLabel(pack({ url: "https://gitlab.com/group/proj.git/" }))).toBe("group/proj");
  });

  it("falls back to the pack name when there is no url", () => {
    expect(sourceLabel(pack({ url: null, name: "Local" }))).toBe("Local");
  });
});

describe("resolveDetailTab", () => {
  it("defaults to the populated kind when there is no pick", () => {
    expect(resolveDetailTab(null, 3, 0)).toBe("agents");
    expect(resolveDetailTab(null, 0, 3)).toBe("skills");   // skill-only pack opens on Skills
  });

  it("honours an explicit pick while that kind has rows", () => {
    expect(resolveDetailTab("skills", 3, 2)).toBe("skills");
    expect(resolveDetailTab("agents", 3, 2)).toBe("agents");
  });

  it("falls back to the populated kind when the pinned kind has emptied (e.g. after a sync)", () => {
    expect(resolveDetailTab("agents", 0, 4)).toBe("skills");
    expect(resolveDetailTab("skills", 5, 0)).toBe("agents");
  });

  it("keeps the pinned kind when BOTH are empty (no better choice to fall back to)", () => {
    expect(resolveDetailTab("agents", 0, 0)).toBe("agents");
  });
});

describe("paginate", () => {
  const items = Array.from({ length: 23 }, (_, i) => i);

  it("returns the requested page-sized slice with the page metadata", () => {
    const p = paginate(items, 0, 10);
    expect(p.items).toEqual([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
    expect(p).toMatchObject({ page: 0, pageCount: 3, total: 23 });
  });

  it("returns the partial last page", () => {
    expect(paginate(items, 2, 10).items).toEqual([20, 21, 22]);
  });

  it("clamps a page past the end back to the last page (a shrunk list can't strand the view)", () => {
    const p = paginate(items, 99, 10);
    expect(p.page).toBe(2);
    expect(p.items).toEqual([20, 21, 22]);
  });

  it("clamps a negative page to 0", () => {
    expect(paginate(items, -3, 10).page).toBe(0);
  });

  it("treats an empty list as a single empty page", () => {
    expect(paginate([], 0, 10)).toEqual({ items: [], page: 0, pageCount: 1, total: 0 });
  });
});
