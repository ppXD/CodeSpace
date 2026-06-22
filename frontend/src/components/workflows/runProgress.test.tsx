import { describe, expect, it } from "vitest";

import type { NodeStatus } from "@/api/workflows";

import { summarizeRunProgress } from "./runProgress";

/** Map literal → the aggregated per-node status map the canvas paints from. */
function statuses(entries: Record<string, NodeStatus>): Map<string, NodeStatus> {
  return new Map(Object.entries(entries));
}

describe("summarizeRunProgress", () => {
  it("returns null before any node has run (all Pending/Skipped)", () => {
    expect(summarizeRunProgress(statuses({ a: "Pending", b: "Skipped" }))).toBeNull();
    expect(summarizeRunProgress(new Map())).toBeNull();
  });

  it("counts done / running / failed and treats a Suspended (parked) node as in-flight", () => {
    const c = summarizeRunProgress(statuses({ a: "Success", b: "Success", c: "Running", d: "Suspended", e: "Failure", f: "Pending" }));
    expect(c).toEqual({ success: 2, running: 2, failure: 1 });
  });

  it("ignores Pending/Skipped nodes in the tally", () => {
    const c = summarizeRunProgress(statuses({ a: "Success", b: "Skipped", c: "Pending" }));
    expect(c).toEqual({ success: 1, running: 0, failure: 0 });
  });
});
