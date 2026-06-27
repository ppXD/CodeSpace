import { describe, expect, it } from "vitest";

import type { PackPreview } from "@/api/packs";
import { defaultSelectedPaths, flagFor } from "./packPreview";

describe("flagFor", () => {
  it("importable → new", () => {
    expect(flagFor({ importable: true, slugConflict: false })).toBe("new");
  });

  it("a handle conflict (not importable) → exists", () => {
    expect(flagFor({ importable: false, slugConflict: true })).toBe("exists");
  });

  it("not importable and no conflict → blocked (e.g. nameless / unparseable)", () => {
    expect(flagFor({ importable: false, slugConflict: false })).toBe("blocked");
  });
});

describe("defaultSelectedPaths", () => {
  const preview: PackPreview = {
    reference: "v1",
    agents: [
      { sourcePath: "agents/new.md", name: "New", derivedSlug: "new", description: null, systemPrompt: "", model: null, tools: null, rawFrontmatterJson: "{}", diagnostics: [], slugConflict: false, importable: true },
      { sourcePath: "agents/dupe.md", name: "Dupe", derivedSlug: "dupe", description: null, systemPrompt: "", model: null, tools: null, rawFrontmatterJson: "{}", diagnostics: [], slugConflict: true, importable: false },
    ],
    skills: [
      { sourcePath: "skills/tdd/SKILL.md", name: "tdd", derivedSlug: "tdd", description: null, body: "", category: null, rawFrontmatterJson: "{}", diagnostics: [], slugConflict: false, importable: true },
      { sourcePath: "skills/nameless/SKILL.md", name: "", derivedSlug: "", description: null, body: "", category: null, rawFrontmatterJson: "{}", diagnostics: ["missing name"], slugConflict: false, importable: false },
    ],
  };

  it("pre-selects exactly the importable agents + skills, across both kinds", () => {
    expect(defaultSelectedPaths(preview).sort()).toEqual(["agents/new.md", "skills/tdd/SKILL.md"]);
  });

  it("selects nothing when nothing is importable", () => {
    expect(defaultSelectedPaths({ reference: null, agents: [], skills: [] })).toEqual([]);
  });
});
