import { describe, expect, it } from "vitest";

import type { WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";

import { bucketRuns, sourceLabel } from "./runsIndex";

function run(id: string, status: WorkflowRunStatus): WorkflowRunSummary {
  return {
    id, workflowId: "w", workflowVersion: 1, sourceType: "manual", status,
    error: null, startedAt: null, completedAt: null, createdDate: "2026-06-22T00:00:00Z",
  };
}

describe("bucketRuns", () => {
  it("splits runs into needs-attention / live / recent by status, preserving order", () => {
    const b = bucketRuns([
      run("a", "Running"), run("b", "Suspended"), run("c", "Success"),
      run("d", "Pending"), run("e", "Failure"), run("f", "Enqueued"), run("g", "Cancelled"),
    ]);

    expect(b.needsAttention.map((r) => r.id)).toEqual(["b"]);              // Suspended = parked on a human
    expect(b.live.map((r) => r.id)).toEqual(["a", "d", "f"]);              // Running / Pending / Enqueued, in input order
    expect(b.recent.map((r) => r.id)).toEqual(["c", "e", "g"]);           // terminal states
  });

  it("returns three empty zones for no runs", () => {
    expect(bucketRuns([])).toEqual({ needsAttention: [], live: [], recent: [] });
  });
});

describe("sourceLabel", () => {
  it("title-cases a source token and falls back for an empty one", () => {
    expect(sourceLabel("manual")).toBe("Manual");
    expect(sourceLabel("schedule.cron")).toBe("Schedule.cron");
    expect(sourceLabel("")).toBe("Run");
  });
});
