import { describe, expect, it } from "vitest";

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

  it("colours a completed hop sage and a failed hop danger, regardless of run liveness", () => {
    expect(runEdgeFlow("Success", "Success", true)).toEqual({ stroke: "#5FA882" });
    expect(runEdgeFlow("Success", "Success", false)).toEqual({ stroke: "#5FA882" });
    expect(runEdgeFlow("Success", "Failure", false)).toEqual({ stroke: "#C0623D" });
  });

  it("leaves an untaken branch unstyled (source not yet succeeded, or target pending/skipped)", () => {
    expect(runEdgeFlow("Running", "Pending", true)).toEqual({});   // nothing flowed out of the source yet
    expect(runEdgeFlow("Success", "Pending", true)).toEqual({});   // target not reached
    expect(runEdgeFlow("Success", "Skipped", true)).toEqual({});
  });
});
