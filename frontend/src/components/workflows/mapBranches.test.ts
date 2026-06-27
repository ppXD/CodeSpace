import { describe, expect, it } from "vitest";

import type { WorkflowRunNodeSummary } from "@/api/workflows";
import { branchBadge, branchGroupKey, fanBranches, fanBreakdown, fanoutSummary, groupMapBranches, nodeIterationLabel, parseIterationKey, parseTurnKey } from "./mapBranches";

/**
 * The map-branch parsing/grouping backbone — pins the engine iteration-key format
 * (`<mapId>#<i>`, nested `<outer>#<i>/<inner>#<j>`) so the run-detail view can badge + group + roll up
 * a fanned-out map. A flow.loop body node shares that `<id>#<i>` shape, so map detection is gated on the
 * backend-stamped `containerKind === "flow.map"` — a loop run produces no branch info and renders flat.
 */

function node(over: Partial<WorkflowRunNodeSummary> & { nodeId: string; iterationKey: string }): WorkflowRunNodeSummary {
  return { containerKind: null, status: "Success", inputs: {}, outputs: {}, error: null, startedAt: null, completedAt: null, childRunId: null, ...over };
}

/** A flow.map element-branch body row (the backend stamps containerKind = "flow.map"). */
function mapNode(over: Partial<WorkflowRunNodeSummary> & { nodeId: string; iterationKey: string }): WorkflowRunNodeSummary {
  return node({ containerKind: "flow.map", ...over });
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
    expect(branchBadge(node({ nodeId: "a", iterationKey: "" }))).toBe("");
  });

  it("renders #i at one level and #i/#j nested for a map row", () => {
    expect(branchBadge(mapNode({ nodeId: "work", iterationKey: "map#2" }))).toBe("#2");
    expect(branchBadge(mapNode({ nodeId: "leaf", iterationKey: "outer#0/inner#3" }))).toBe("#0/#3");
  });

  it("is empty for a LOOP body row even though its key has the same `<id>#<i>` shape", () => {
    // The engine keys a loop body node "loop#0" exactly like a map branch — only containerKind disambiguates.
    expect(branchBadge(node({ nodeId: "step", iterationKey: "loop#0", containerKind: "flow.loop" }))).toBe("");
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

  it("returns no groups for a LOOP run (same key shape, but containerKind is flow.loop)", () => {
    const rollups = groupMapBranches([
      node({ nodeId: "step", iterationKey: "loop#0", containerKind: "flow.loop" }),
      node({ nodeId: "step", iterationKey: "loop#1", containerKind: "flow.loop" }),
      node({ nodeId: "step", iterationKey: "loop#2", containerKind: "flow.loop", status: "Failure" }),
    ]);
    expect(rollups).toEqual([]);
  });

  it("rolls up a K-branch map: total elements, done, and failed (per distinct branch, not per row)", () => {
    const nodes = [
      // branch 0: two body rows, both succeed → one done branch
      mapNode({ nodeId: "start", iterationKey: "map#0" }),
      mapNode({ nodeId: "work", iterationKey: "map#0" }),
      // branch 1: succeeds
      mapNode({ nodeId: "start", iterationKey: "map#1" }),
      // branch 2: a row failed → the whole branch counts as failed
      mapNode({ nodeId: "work", iterationKey: "map#2", status: "Failure" }),
    ];
    const rollups = groupMapBranches(nodes);

    expect(rollups).toHaveLength(1);
    expect(rollups[0]).toMatchObject({ mapId: "map", total: 3, done: 2, failed: 1 });
    expect(rollups[0].branchIndices).toEqual([0, 1, 2]);
  });

  it("counts an in-flight branch in total but NOT in done (live accuracy)", () => {
    // A 3-element map mid-run: one finished cleanly, one failed, one still Running.
    const nodes = [
      mapNode({ nodeId: "work", iterationKey: "map#0", status: "Success" }),
      mapNode({ nodeId: "work", iterationKey: "map#1", status: "Failure" }),
      mapNode({ nodeId: "work", iterationKey: "map#2", status: "Running" }),
    ];
    const rollups = groupMapBranches(nodes);

    // The running branch is neither done nor failed — "1/3 done", not a misleading "3/3" / "2/3".
    expect(rollups[0]).toMatchObject({ mapId: "map", total: 3, done: 1, failed: 1 });
  });

  it("treats a branch with a Suspended row as in-flight (not done)", () => {
    // A parked map suspends its branch rows; a Suspended row must not read as done.
    const nodes = [
      mapNode({ nodeId: "approve", iterationKey: "map#0", status: "Suspended" }),
      mapNode({ nodeId: "approve", iterationKey: "map#1", status: "Success" }),
    ];
    const rollups = groupMapBranches(nodes);

    expect(rollups[0]).toMatchObject({ mapId: "map", total: 2, done: 1, failed: 0 });
  });

  it("only counts a multi-row branch done once ALL its rows are terminal", () => {
    // branch 0 has a finished row and a still-running row → not done yet.
    const nodes = [
      mapNode({ nodeId: "fetch", iterationKey: "map#0", status: "Success" }),
      mapNode({ nodeId: "work", iterationKey: "map#0", status: "Running" }),
    ];
    const rollups = groupMapBranches(nodes);

    expect(rollups[0]).toMatchObject({ mapId: "map", total: 1, done: 0, failed: 0 });
  });

  it("keeps nested map-in-map branches in distinct groups (per outer pass + inner map)", () => {
    const nodes = [
      mapNode({ nodeId: "leaf", iterationKey: "outer#0/inner#0" }),
      mapNode({ nodeId: "leaf", iterationKey: "outer#0/inner#1" }),
      mapNode({ nodeId: "leaf", iterationKey: "outer#1/inner#0" }),
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
      mapNode({ nodeId: "body", iterationKey: "map#0" }),
      mapNode({ nodeId: "body", iterationKey: "map#1" }),
    ];
    const rollups = groupMapBranches(nodes);
    expect(rollups).toHaveLength(1);
    expect(rollups[0]).toMatchObject({ mapId: "map", total: 2, done: 2, failed: 0 });
  });
});

describe("fanBranches / isMapFanout / fanBreakdown", () => {
  it("returns one branch per element, ascending by index, with its badge + row", () => {
    const rows = [
      mapNode({ nodeId: "agent", iterationKey: "map#2", status: "Running" }),
      mapNode({ nodeId: "agent", iterationKey: "map#0", status: "Success" }),
      mapNode({ nodeId: "agent", iterationKey: "map#1", status: "Failure" }),
    ];
    const branches = fanBranches(rows);
    expect(branches.map((b) => b.index)).toEqual([0, 1, 2]);          // sorted by element index, NOT input order
    expect(branches.map((b) => b.badge)).toEqual(["#0", "#1", "#2"]);
    expect(branches[0].row.status).toBe("Success");                  // the #0 row, not the array's first
  });

  it("ignores loop / try / flat rows (gated on containerKind)", () => {
    expect(fanBranches([node({ nodeId: "body", iterationKey: "loop#0" })])).toEqual([]);   // containerKind null → not a map
    expect(fanBranches([mapNode({ nodeId: "agent", iterationKey: "map#0" })])).toHaveLength(1);   // a real flow.map branch counts
  });

  it("the FIRST row wins when a branch index recurs (a multi-node body)", () => {
    const rows = [
      mapNode({ nodeId: "ms", iterationKey: "map#0", status: "Success" }),
      mapNode({ nodeId: "agent", iterationKey: "map#0", status: "Failure" }),
    ];
    const branches = fanBranches(rows);
    expect(branches).toHaveLength(1);
    expect(branches[0].row.nodeId).toBe("ms");
  });

  it("buckets running (incl Suspended), done (incl Skipped), failed, queued", () => {
    const rows = [
      mapNode({ nodeId: "a", iterationKey: "map#0", status: "Success" }),
      mapNode({ nodeId: "a", iterationKey: "map#1", status: "Running" }),
      mapNode({ nodeId: "a", iterationKey: "map#2", status: "Suspended" }),
      mapNode({ nodeId: "a", iterationKey: "map#3", status: "Failure" }),
      mapNode({ nodeId: "a", iterationKey: "map#4", status: "Pending" }),
      mapNode({ nodeId: "a", iterationKey: "map#5", status: "Skipped" }),
    ];
    expect(fanBreakdown(fanBranches(rows))).toEqual({ total: 6, done: 2, running: 2, failed: 1, queued: 1 });
  });
});

describe("supervisor turn legibility", () => {
  it("parses <id>#turn{N} and its #park/#ask sub-state, rejecting non-turn keys", () => {
    expect(parseTurnKey("sup#turn1")).toEqual({ turn: 1, sub: undefined });
    expect(parseTurnKey("sup#turn2#park")).toEqual({ turn: 2, sub: "park" });
    expect(parseTurnKey("sup#turn5#ask")).toEqual({ turn: 5, sub: "ask" });
    expect(parseTurnKey("map#0")).toBeNull();   // a map branch is not a turn
    expect(parseTurnKey("")).toBeNull();        // a top-level node
  });

  it("labels each row distinctly: map branch #i, supervisor turn, nothing for plain/loop rows", () => {
    expect(nodeIterationLabel(mapNode({ nodeId: "a", iterationKey: "map#2" }))).toBe("#2");
    expect(nodeIterationLabel(node({ nodeId: "sup", iterationKey: "sup#turn1" }))).toBe("turn 1");
    expect(nodeIterationLabel(node({ nodeId: "sup", iterationKey: "sup#turn2#park" }))).toBe("turn 2 · parked");
    expect(nodeIterationLabel(node({ nodeId: "sup", iterationKey: "sup#turn5#ask" }))).toBe("turn 5 · awaiting input");
    expect(nodeIterationLabel(node({ nodeId: "body", iterationKey: "loop#0" }))).toBe("");   // loop pass stays unlabeled
    expect(nodeIterationLabel(node({ nodeId: "start", iterationKey: "" }))).toBe("");        // top-level
  });

  it("counts a supervisor node's footer as TURNS (distinct turn numbers, ignoring #park/#ask), not branches", () => {
    const rows = [
      node({ nodeId: "sup", iterationKey: "" }),              // the top-level supervisor cell
      node({ nodeId: "sup", iterationKey: "sup#turn1" }),
      node({ nodeId: "sup", iterationKey: "sup#turn2#park" }),
      node({ nodeId: "sup", iterationKey: "sup#turn3" }),
    ];
    expect(fanoutSummary(rows)).toEqual({ count: 3, noun: "turns" });   // 3 distinct turns, NOT "4 branches"
  });

  it("still calls a flow.map's footer BRANCHES", () => {
    const rows = [mapNode({ nodeId: "a", iterationKey: "map#0" }), mapNode({ nodeId: "a", iterationKey: "map#1" })];
    expect(fanoutSummary(rows)).toEqual({ count: 2, noun: "branches" });
  });

  it("a single-run node reports count 1 (no fan-out noun shown by the caller)", () => {
    expect(fanoutSummary([node({ nodeId: "start", iterationKey: "" })])).toEqual({ count: 1, noun: "run" });
  });
});
