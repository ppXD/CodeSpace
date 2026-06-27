import { describe, expect, it } from "vitest";

import type { AgentPackPreviewItem, PackSyncResult, SkillPackPreviewItem } from "@/api/packs";

import { newArtifactCount, syncSummaryLabel } from "./syncView";

const agent = (sourcePath: string): AgentPackPreviewItem => ({
  sourcePath, name: sourcePath, derivedSlug: sourcePath, description: null, systemPrompt: "",
  model: null, tools: null, rawFrontmatterJson: "{}", diagnostics: [], slugConflict: false, importable: true,
});
const skill = (sourcePath: string): SkillPackPreviewItem => ({
  sourcePath, name: sourcePath, derivedSlug: sourcePath, description: null, body: "",
  category: null, rawFrontmatterJson: "{}", diagnostics: [], slugConflict: false, importable: true,
});

const result = (over: Partial<PackSyncResult>): PackSyncResult => ({
  packId: "p1", reference: "main", upToDate: 0, updated: 0,
  newArtifacts: { reference: "main", agents: [], skills: [] }, ...over,
});

describe("newArtifactCount", () => {
  it("sums agents + skills", () => {
    expect(newArtifactCount(result({ newArtifacts: { reference: "main", agents: [agent("a"), agent("b")], skills: [skill("s")] } }))).toBe(3);
  });

  it("is zero with no new artifacts", () => {
    expect(newArtifactCount(result({}))).toBe(0);
  });
});

describe("syncSummaryLabel", () => {
  it("reports nothing-changed plainly", () => {
    expect(syncSummaryLabel(result({ upToDate: 5, updated: 0 }))).toBe("Already up to date");
    expect(syncSummaryLabel(result({ upToDate: 0, updated: 0 }))).toBe("Already up to date");
  });

  it("joins the non-zero parts", () => {
    expect(syncSummaryLabel(result({ upToDate: 12, updated: 2 }))).toBe("12 up to date · 2 updated");
  });

  it("includes the new count", () => {
    const r = result({ upToDate: 4, updated: 1, newArtifacts: { reference: "main", agents: [agent("a")], skills: [skill("s"), skill("t")] } });
    expect(syncSummaryLabel(r)).toBe("4 up to date · 1 updated · 3 new");
  });

  it("omits a zero up-to-date when something changed", () => {
    expect(syncSummaryLabel(result({ upToDate: 0, updated: 3 }))).toBe("3 updated");
  });
});
