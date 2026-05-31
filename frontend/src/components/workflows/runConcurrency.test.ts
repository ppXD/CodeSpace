import { describe, expect, it } from "vitest";

import type { WorkflowRunNodeSummary } from "@/api/workflows";

import { concurrentNodeKeys, runNodeKey } from "./runConcurrency";

// Minimal node-summary builder — only the fields concurrentNodeKeys reads matter.
const node = (
  nodeId: string,
  startedAt: string | null,
  completedAt: string | null,
  iterationKey = "",
): WorkflowRunNodeSummary => ({
  nodeId, iterationKey, status: "Success", inputs: {}, outputs: {}, error: null, startedAt, completedAt,
});

const T = (sec: number) => new Date(Date.UTC(2026, 0, 1, 0, 0, sec)).toISOString();

describe("concurrentNodeKeys", () => {
  it("flags both nodes when their execution intervals overlap", () => {
    const out = concurrentNodeKeys([
      node("a", T(0), T(10)),
      node("b", T(5), T(15)), // starts before a ends → overlap
    ]);
    expect(out).toEqual(new Set(["a:", "b:"]));
  });

  it("returns empty for a sequential run (touching handoff is NOT overlap)", () => {
    const out = concurrentNodeKeys([
      node("a", T(0), T(10)),
      node("b", T(10), T(20)), // b starts exactly when a ends
    ]);
    expect(out.size).toBe(0);
  });

  it("returns empty for a sequential run with a gap", () => {
    expect(concurrentNodeKeys([node("a", T(0), T(5)), node("b", T(8), T(12))]).size).toBe(0);
  });

  it("isolates the overlapping pair from a separate sequential node", () => {
    const out = concurrentNodeKeys([
      node("a", T(0), T(10)),
      node("b", T(2), T(8)),   // overlaps a
      node("c", T(20), T(25)), // runs later, alone
    ]);
    expect(out).toEqual(new Set(["a:", "b:"]));
  });

  it("ignores nodes that never started (no interval to compare)", () => {
    const out = concurrentNodeKeys([
      node("a", T(0), T(10)),
      node("skipped", null, null),
    ]);
    expect(out.size).toBe(0);
  });

  it("treats a still-running node (no completedAt) as an instant that can fall inside another's window", () => {
    const out = concurrentNodeKeys([
      node("long", T(0), T(10)),
      node("running", T(5), null), // [5,5] sits inside [0,10]
    ]);
    expect(out).toEqual(new Set(["long:", "running:"]));
  });

  it("keys concurrent body nodes by node id AND iteration key", () => {
    const out = concurrentNodeKeys([
      node("pa", T(0), T(10), "loop#0"),
      node("pb", T(1), T(9), "loop#0"),
    ]);
    expect(out).toEqual(new Set(["pa:loop#0", "pb:loop#0"]));
    expect(runNodeKey(node("pa", T(0), T(1), "loop#0"))).toBe("pa:loop#0");
  });
});
