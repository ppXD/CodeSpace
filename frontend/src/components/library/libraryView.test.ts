import { describe, expect, it } from "vitest";

import type { PackArtifactSummary, PackSummary } from "@/api/packs";

import { countLabel, resolveSelectedPackId, sourceLabel, splitArtifacts } from "./libraryView";

const pack = (over: Partial<PackSummary>): PackSummary => ({
  id: "p1", kind: "Github", name: "agents", url: null, reference: null,
  lastSyncedSha: null, lastSyncedDate: null, agentCount: 0, skillCount: 0, ...over,
});

const artifact = (kind: "Agent" | "Skill", slug: string): PackArtifactSummary => ({
  kind, id: slug, slug, name: slug, description: null, sourcePath: `${slug}.md`,
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

describe("splitArtifacts", () => {
  it("partitions by kind, preserving order within each section", () => {
    const { agents, skills } = splitArtifacts([
      artifact("Agent", "a1"), artifact("Skill", "s1"), artifact("Agent", "a2"), artifact("Skill", "s2"),
    ]);

    expect(agents.map((a) => a.slug)).toEqual(["a1", "a2"]);
    expect(skills.map((s) => s.slug)).toEqual(["s1", "s2"]);
  });

  it("handles an empty pack", () => {
    expect(splitArtifacts([])).toEqual({ agents: [], skills: [] });
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
