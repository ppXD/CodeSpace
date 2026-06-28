import { describe, expect, it } from "vitest";

import type { PackPreview } from "@/api/packs";
import { defaultSelectedPaths, filterRows, flagFor, type Row } from "./packPreview";

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

describe("filterRows", () => {
  const row = (over: Partial<Row>): Row => ({ sourcePath: "p", name: "n", derivedSlug: "s", description: null, diagnostics: [], slugConflict: false, importable: true, kind: "skill", ...over });
  const rows = [
    row({ name: "Test Driven Development", derivedSlug: "test-driven-development", sourcePath: "skills/tdd/SKILL.md" }),
    row({ name: "Brainstorming", derivedSlug: "brainstorming", sourcePath: "skills/brainstorm/SKILL.md" }),
  ];

  it("matches name, @handle, or source path (case-insensitive)", () => {
    expect(filterRows(rows, "driven").map((r) => r.derivedSlug)).toEqual(["test-driven-development"]);
    expect(filterRows(rows, "BRAINSTORM").map((r) => r.derivedSlug)).toEqual(["brainstorming"]);
    expect(filterRows(rows, "tdd").map((r) => r.derivedSlug)).toEqual(["test-driven-development"]); // source path
  });

  it("returns all rows for a blank query", () => {
    expect(filterRows(rows, "   ")).toHaveLength(2);
  });

  it("returns none when nothing matches", () => {
    expect(filterRows(rows, "zzz")).toEqual([]);
  });
});
