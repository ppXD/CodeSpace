import { describe, expect, it } from "vitest";

import { readWorkspaceRepos, writeWorkspaceRepos, type WorkspaceRepoRow } from "./nodeRepoWorkspace";

/**
 * The two pure adapters that let the unified RepositoryWorkspacePicker present ONE flat repo list while
 * persisting the engine's existing { repositoryId (scalar primary), relatedRepositories[] } shape.
 *
 * These tests are the non-breaking contract: what the picker loads must re-emit byte-identical, and the
 * primary's alias/access must never leak into the persisted shape (the fold has no slot for them).
 */

describe("readWorkspaceRepos", () => {
  it("scalar primary → one primary row (writable, no alias)", () => {
    expect(readWorkspaceRepos("p", undefined)).toEqual([{ repositoryId: "p", alias: "", access: "write" }]);
  });

  it("primary + related → primary row first, then related rows in order", () => {
    const rows = readWorkspaceRepos("p", [
      { repositoryId: "a", alias: "api", access: "write" },
      { repositoryId: "b", access: "read" },
    ]);
    expect(rows).toEqual([
      { repositoryId: "p", alias: "", access: "write" },
      { repositoryId: "a", alias: "api", access: "write" },
      { repositoryId: "b", alias: "", access: "read" },
    ]);
  });

  it("no primary → empty list (analysis-only run)", () => {
    expect(readWorkspaceRepos("", undefined)).toEqual([]);
    expect(readWorkspaceRepos(undefined, undefined)).toEqual([]);
  });

  it("related with absent access defaults to read; malformed entries are dropped", () => {
    const rows = readWorkspaceRepos("p", [{ repositoryId: "a" }, null, "nope", { repositoryId: 42 }]);
    expect(rows).toEqual([
      { repositoryId: "p", alias: "", access: "write" },
      { repositoryId: "a", alias: "", access: "read" },
      { repositoryId: "", alias: "", access: "read" }, // non-string id → in-progress empty row
    ]);
  });
});

describe("writeWorkspaceRepos", () => {
  it("row 0 → scalar primary; its alias/access are dropped (no slot in the persisted shape)", () => {
    const rows: WorkspaceRepoRow[] = [{ repositoryId: "p", alias: "should-drop", access: "read" }];
    expect(writeWorkspaceRepos(rows)).toEqual({ repositoryId: "p", relatedRepositories: undefined });
  });

  it("rows 1+ → relatedRepositories with access always present, blank alias omitted", () => {
    const rows: WorkspaceRepoRow[] = [
      { repositoryId: "p", alias: "", access: "write" },
      { repositoryId: "a", alias: "api", access: "write" },
      { repositoryId: "b", alias: "  ", access: "read" }, // whitespace alias → omitted
    ];
    expect(writeWorkspaceRepos(rows)).toEqual({
      repositoryId: "p",
      relatedRepositories: [
        { repositoryId: "a", access: "write", alias: "api" },
        { repositoryId: "b", access: "read" },
      ],
    });
  });

  it("empty list → both keys undefined (drop → analysis-only, byte-identical)", () => {
    expect(writeWorkspaceRepos([])).toEqual({ repositoryId: undefined, relatedRepositories: undefined });
  });
});

describe("round-trip byte-identity", () => {
  it("primary + related survives read→write unchanged", () => {
    const repositoryId = "3f2a7c19-0b44-4e51-9a0e-1d2c3b4a5f60";
    const relatedRepositories = [
      { repositoryId: "9c1b2d3e-4f50-6172-8394-a5b6c7d8e9f0", alias: "api", access: "write" },
      { repositoryId: "7e4d5c6b-3a29-1807-6f5e-4d3c2b1a0987", access: "read" },
    ];

    const emitted = writeWorkspaceRepos(readWorkspaceRepos(repositoryId, relatedRepositories));

    expect(emitted.repositoryId).toBe(repositoryId);
    expect(emitted.relatedRepositories).toEqual(relatedRepositories);
  });

  it("single-repo survives with no related key (undefined)", () => {
    const emitted = writeWorkspaceRepos(readWorkspaceRepos("only", undefined));
    expect(emitted).toEqual({ repositoryId: "only", relatedRepositories: undefined });
  });
});
