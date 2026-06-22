import { describe, expect, it } from "vitest";

import { buildRunListParams, type RunListFilterInput } from "./workflows";

/**
 * buildRunListParams is the single bridge between the filter UI and the runs endpoint — and the basis of the React
 * Query cache key, so it must be CANONICAL (equivalent filters → identical strings) and OMIT empty dimensions (so an
 * untouched filter is just `limit=...`, sharing the unfiltered cache entry). These guard both contracts.
 */
describe("buildRunListParams", () => {
  const parse = (s: string) => new URLSearchParams(s);

  it("emits only limit for an empty / undefined filter", () => {
    expect(buildRunListParams(undefined, 50)).toBe("limit=50");
    expect(buildRunListParams({}, 50)).toBe("limit=50");
  });

  it("omits empty arrays (an untouched dimension is no constraint)", () => {
    const f: RunListFilterInput = { repositoryIds: [], statuses: [] };
    expect(buildRunListParams(f, 50)).toBe("limit=50");
  });

  it("emits one repeated param per list value (OR-within a dimension)", () => {
    const p = parse(buildRunListParams({ repositoryIds: ["r1", "r2"] }, 50));
    expect(p.getAll("repositoryIds")).toEqual(["r1", "r2"]);
  });

  it("carries every dimension AND date / boolean bounds", () => {
    const f: RunListFilterInput = {
      runKinds: ["task"],
      repositoryIds: ["r1"],
      projectIds: ["p1"],
      actorIds: ["u1"],
      agentDefinitionIds: ["a1"],
      statuses: ["Running"],
      needsAttention: true,
      hasPendingDecision: false,
      since: "2026-01-01T00:00:00.000Z",
    };
    const p = parse(buildRunListParams(f, 25));

    expect(p.get("limit")).toBe("25");
    expect(p.get("runKinds")).toBe("task");
    expect(p.get("repositoryIds")).toBe("r1");
    expect(p.get("projectIds")).toBe("p1");
    expect(p.get("actorIds")).toBe("u1");
    expect(p.get("agentDefinitionIds")).toBe("a1");
    expect(p.get("statuses")).toBe("Running");
    expect(p.get("needsAttention")).toBe("true");
    expect(p.get("hasPendingDecision")).toBe("false");      // false is meaningful — must be emitted, not dropped
    expect(p.get("since")).toBe("2026-01-01T00:00:00.000Z");
  });

  it("appends the cursor when present", () => {
    expect(parse(buildRunListParams({}, 50, "CUR")).get("cursor")).toBe("CUR");
    expect(parse(buildRunListParams({}, 50)).has("cursor")).toBe(false);
  });

  it("is canonical — equal filters serialize identically (the cache-key invariant)", () => {
    const a: RunListFilterInput = { repositoryIds: ["r1"], runKinds: ["task"] };
    const b: RunListFilterInput = { runKinds: ["task"], repositoryIds: ["r1"] };   // same dims, different key order
    expect(buildRunListParams(a, 50)).toBe(buildRunListParams(b, 50));
  });
});
