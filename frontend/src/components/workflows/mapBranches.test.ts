import { describe, expect, it } from "vitest";

import type { WorkflowRunNodeSummary } from "@/api/workflows";
import { branchBadge, branchGroupKey, groupMapBranches, parseIterationKey } from "./mapBranches";

/**
 * The map-branch parsing/grouping backbone — pins the engine iteration-key format
 * (`<mapId>#<i>`, nested `<outer>#<i>/<inner>#<j>`) so the run-detail view can badge + group + roll up
 * a fanned-out map. A non-iterated node has an empty key, so a non-map run produces zero branch info.
 */

function node(over: Partial<WorkflowRunNodeSummary> & { nodeId: string; iterationKey: string }): WorkflowRunNodeSummary {
  return { status: "Success", inputs: {}, outputs: {}, error: null, startedAt: null, completedAt: null, childRunId: null, ...over };
}

describe("parseIterationKey", () => {
  it("returns no segments for a top-level (empty) key", () => {
    expect(parseIterationKey("")).toEqual([]);
  });

  it("parses a single-level map branch key", () => {
    expect(parseIterationKey("map#2")).toEqual([{ containerId: "map", index: 2 }]);
  });

  it("parses a nested map-in-map key into ordered segments", () => {
    expect(parseIterationKey("outer#0/inner#3")).toEqual([
      { containerId: "outer", index: 0 },
      { containerId: "inner", index: 3 },
    ]);
  });

  it("tolerates a container id that itself contains a '#' (splits on the LAST one)", () => {
    expect(parseIterationKey("we#ird#5")).toEqual([{ containerId: "we#ird", index: 5 }]);
  });

  it("skips a malformed segment instead of throwing (forward-compatible)", () => {
    expect(parseIterationKey("map#x")).toEqual([]);
    expect(parseIterationKey("noHashHere")).toEqual([]);
  });
});

describe("branchBadge", () => {
  it("is empty for a top-level node", () => {
    expect(branchBadge("")).toBe("");
  });

  it("renders #i at one level and #i/#j nested", () => {
    expect(branchBadge("map#2")).toBe("#2");
    expect(branchBadge("outer#0/inner#3")).toBe("#0/#3");
  });
});

describe("branchGroupKey", () => {
  it("strips the leaf index so siblings of one map share a group", () => {
    expect(branchGroupKey("map#0")).toBe("map");
    expect(branchGroupKey("map#7")).toBe("map");
    expect(branchGroupKey("outer#1/inner#2")).toBe("outer#1/inner");
    expect(branchGroupKey("")).toBe("");
  });
});

describe("groupMapBranches", () => {
  it("returns no groups for a non-map run (all empty iteration keys)", () => {
    const rollups = groupMapBranches([node({ nodeId: "a", iterationKey: "" }), node({ nodeId: "b", iterationKey: "" })]);
    expect(rollups).toEqual([]);
  });

  it("rolls up a K-branch map: total elements, done, and failed (per distinct branch, not per row)", () => {
    const nodes = [
      // branch 0: two body rows, both succeed → one done branch
      node({ nodeId: "start", iterationKey: "map#0" }),
      node({ nodeId: "work", iterationKey: "map#0" }),
      // branch 1: succeeds
      node({ nodeId: "start", iterationKey: "map#1" }),
      // branch 2: a row failed → the whole branch counts as failed
      node({ nodeId: "work", iterationKey: "map#2", status: "Failure" }),
    ];
    const rollups = groupMapBranches(nodes);

    expect(rollups).toHaveLength(1);
    expect(rollups[0]).toMatchObject({ mapId: "map", total: 3, done: 2, failed: 1 });
    expect(rollups[0].branchIndices).toEqual([0, 1, 2]);
  });

  it("keeps nested map-in-map branches in distinct groups (per outer pass + inner map)", () => {
    const nodes = [
      node({ nodeId: "leaf", iterationKey: "outer#0/inner#0" }),
      node({ nodeId: "leaf", iterationKey: "outer#0/inner#1" }),
      node({ nodeId: "leaf", iterationKey: "outer#1/inner#0" }),
    ];
    const rollups = groupMapBranches(nodes);

    // outer#0's inner map (2 branches) and outer#1's inner map (1 branch) are SEPARATE groups.
    expect(rollups).toHaveLength(2);
    expect(rollups.map((r) => r.total).sort()).toEqual([1, 2]);
    expect(rollups.every((r) => r.mapId === "inner")).toBe(true);
  });

  it("ignores top-level (non-map) rows mixed in with map rows", () => {
    const nodes = [
      node({ nodeId: "trigger", iterationKey: "" }),
      node({ nodeId: "synth", iterationKey: "" }),
      node({ nodeId: "body", iterationKey: "map#0" }),
      node({ nodeId: "body", iterationKey: "map#1" }),
    ];
    const rollups = groupMapBranches(nodes);
    expect(rollups).toHaveLength(1);
    expect(rollups[0]).toMatchObject({ mapId: "map", total: 2, done: 2, failed: 0 });
  });
});
