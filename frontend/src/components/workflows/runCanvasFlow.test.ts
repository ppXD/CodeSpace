import { describe, expect, it } from "vitest";

import type { NodeStatus } from "@/api/workflows";
import { runEdgeFlow } from "./runCanvasFlow";

describe("runEdgeFlow", () => {
  it("animates the terracotta flow into a live node while the run is active", () => {
    expect(runEdgeFlow("Success", "Running", true)).toEqual({ stroke: "#D97757", animated: true });
    expect(runEdgeFlow("Success", "Suspended", true)).toEqual({ stroke: "#D97757", animated: true });
  });

  it("shows a STATIC stopped line into a stale-active node once the run is terminal (e.g. cancelled)", () => {
    // A node still reading Suspended/Running on a finished run is stale — no live flow, no animation.
    expect(runEdgeFlow("Success", "Suspended", false)).toEqual({ stroke: "#B7AEA1" });
    expect(runEdgeFlow("Success", "Running", false)).toEqual({ stroke: "#B7AEA1" });
    expect(runEdgeFlow("Success", "Running", false).animated).toBeUndefined();
  });

  it("colours a failed hop danger, regardless of run liveness", () => {
    expect(runEdgeFlow("Success", "Failure", true)).toEqual({ stroke: "#C0623D" });
    expect(runEdgeFlow("Success", "Failure", false)).toEqual({ stroke: "#C0623D" });
  });

  // The run-state grammar (C1): each completed/declined hop carries a semantic `cls` on top of its stroke.
  it("tags the edge's run-state class", () => {
    const cases: Array<{ name: string; src?: NodeStatus; tgt?: NodeStatus; runActive: boolean; expected: ReturnType<typeof runEdgeFlow> }> = [
      // taken — both endpoints Success, the hop the run walked: sage stroke + `taken`, on a live OR terminal run.
      { name: "taken (live)", src: "Success", tgt: "Success", runActive: true, expected: { stroke: "#5FA882", cls: "taken" } },
      { name: "taken (terminal)", src: "Success", tgt: "Success", runActive: false, expected: { stroke: "#5FA882", cls: "taken" } },
      // dead — source Success + target Skipped, a branch the run declined: muted stroke + `dead` (canvas dims it).
      { name: "dead (live)", src: "Success", tgt: "Skipped", runActive: true, expected: { stroke: "#B7AEA1", cls: "dead" } },
      { name: "dead (terminal)", src: "Success", tgt: "Skipped", runActive: false, expected: { stroke: "#B7AEA1", cls: "dead" } },
      // No verdict class is ever emitted here — a "just-settled" signal isn't derivable from the two statuses
      // alone (see runEdgeFlow); a completed hop stays `taken`. B7 owns the verdict flash.
    ];

    for (const c of cases) expect(runEdgeFlow(c.src, c.tgt, c.runActive), c.name).toEqual(c.expected);
  });

  it("never fakes a verdict class from the endpoint statuses alone", () => {
    expect(runEdgeFlow("Success", "Success", true).cls).toBe("taken");
    expect(runEdgeFlow("Success", "Success", false).cls).toBe("taken");
  });

  it("leaves an unreached branch unstyled (source not yet succeeded, or target still pending)", () => {
    expect(runEdgeFlow("Running", "Pending", true)).toEqual({});   // nothing flowed out of the source yet
    expect(runEdgeFlow("Success", "Pending", true)).toEqual({});   // target not reached
  });
});
