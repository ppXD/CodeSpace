import { describe, expect, it } from "vitest";

import type { PendingDecision, WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";

import { compactAge, formatDuration, runDuration, runStatusTone, runStatusWord, runType, summarizeDecisions, summarizeToday } from "./cockpit";

function decision(o: Partial<PendingDecision>): PendingDecision {
  return {
    id: "d", grain: "tool_ledger", rootTraceId: "r", decisionType: "confirm", question: "?",
    options: [], riskLevel: "low", policy: "human_required", createdAt: "2026-06-22T00:00:00Z", ...o,
  };
}

function run(id: string, status: WorkflowRunStatus, createdDate = "2026-06-22T00:00:00Z"): WorkflowRunSummary {
  return { id, workflowId: "w", workflowVersion: 1, workflowName: null, sourceType: "manual", status, error: null, startedAt: null, completedAt: null, createdDate };
}

describe("formatDuration", () => {
  const t0 = "2026-06-22T00:00:00Z";
  it("reads in s / m s / h m, and is empty without both ends", () => {
    expect(formatDuration(t0, "2026-06-22T00:00:45Z")).toBe("45s");
    expect(formatDuration(t0, "2026-06-22T00:07:59Z")).toBe("7m 59s");
    expect(formatDuration(t0, "2026-06-22T01:05:00Z")).toBe("1h 5m");
    expect(formatDuration(null, t0)).toBe("");
    expect(formatDuration(t0, null)).toBe("");
  });
});

describe("runType", () => {
  it("is Workflow with a parent workflow, Task without one", () => {
    expect(runType(run("a", "Success"))).toBe("Workflow");                        // run() sets workflowId: "w"
    expect(runType({ ...run("b", "Success"), workflowId: null })).toBe("Task");
  });
});

describe("runStatusTone", () => {
  it("maps each run status to its tone", () => {
    expect(runStatusTone("Success")).toBe("ok");
    expect(runStatusTone("Failure")).toBe("err");
    expect(runStatusTone("Running")).toBe("running");
    expect(runStatusTone("Suspended")).toBe("suspended");
    expect(runStatusTone("Cancelled")).toBe("cancelled");
    expect(runStatusTone("Pending")).toBe("queued");
    expect(runStatusTone("Enqueued")).toBe("queued");
  });
});

describe("runStatusWord", () => {
  it("softens Failure→Failed and Enqueued→Queued, passes the rest through", () => {
    expect(runStatusWord("Failure")).toBe("Failed");
    expect(runStatusWord("Enqueued")).toBe("Queued");
    expect(runStatusWord("Success")).toBe("Success");
    expect(runStatusWord("Running")).toBe("Running");
    expect(runStatusWord("Cancelled")).toBe("Cancelled");
  });
});

describe("runDuration", () => {
  const now = Date.parse("2026-06-22T01:00:00Z");   // 1h after run()'s default createdDate
  it("shows total duration for a terminal run, elapsed for live, wait age for parked, empty when not meaningful", () => {
    expect(runDuration({ ...run("a", "Success"), completedAt: "2026-06-22T00:07:59Z" }, now)).toBe("7m 59s");
    expect(runDuration({ ...run("b", "Failure"), completedAt: "2026-06-22T00:04:11Z" }, now)).toBe("4m 11s");
    expect(runDuration({ ...run("g", "Cancelled"), completedAt: "2026-06-22T00:02:00Z" }, now)).toBe("2m 0s");
    expect(runDuration(run("c", "Success"), now)).toBe("");                                              // terminal, no completedAt
    expect(runDuration({ ...run("d", "Running"), startedAt: "2026-06-22T00:58:00Z" }, now)).toBe("2m");  // elapsed from startedAt
    expect(runDuration({ ...run("e", "Suspended"), createdDate: "2026-06-21T22:00:00Z" }, now)).toBe("waiting 3h");
    expect(runDuration(run("f", "Pending"), now)).toBe("");
  });
});

describe("compactAge", () => {
  const now = 1_700_000_000_000;
  it("reads in the right unit, 'now' under a minute", () => {
    expect(compactAge(new Date(now - 30_000).toISOString(), now)).toBe("now");
    expect(compactAge(new Date(now - 14 * 60_000).toISOString(), now)).toBe("14m");
    expect(compactAge(new Date(now - 3 * 3_600_000).toISOString(), now)).toBe("3h");
    expect(compactAge(new Date(now - 2 * 86_400_000).toISOString(), now)).toBe("2d");
  });
});

describe("summarizeDecisions", () => {
  const now = 1_700_000_000_000;
  it("is all-zero for an empty queue", () => {
    expect(summarizeDecisions([], now)).toEqual({ count: 0, oldestAge: null, highRisk: 0 });
  });

  it("counts, ages the oldest, and tallies high-risk", () => {
    const s = summarizeDecisions([
      decision({ id: "a", createdAt: new Date(now - 5 * 60_000).toISOString(), riskLevel: "high" }),
      decision({ id: "b", createdAt: new Date(now - 14 * 60_000).toISOString(), riskLevel: "low" }),
      decision({ id: "c", createdAt: new Date(now - 2 * 60_000).toISOString(), riskLevel: "High" }),
    ], now);
    expect(s.count).toBe(3);
    expect(s.oldestAge).toBe("14m");   // the b decision is oldest
    expect(s.highRisk).toBe(2);        // a + c (case-insensitive)
  });
});

describe("summarizeToday", () => {
  it("counts only today's runs and histograms them by hour", () => {
    const now = new Date(2026, 5, 22, 15, 0, 0).getTime();   // local noon-ish
    const startOfDay = new Date(2026, 5, 22, 0, 0, 0).getTime();
    const s = summarizeToday([
      run("a", "Success", new Date(startOfDay + 2 * 3_600_000).toISOString()),   // 02:00 today
      run("b", "Success", new Date(startOfDay + 2 * 3_600_000 + 60_000).toISOString()),
      run("c", "Success", new Date(startOfDay + 9 * 3_600_000).toISOString()),   // 09:00 today
      run("d", "Success", new Date(startOfDay - 3_600_000).toISOString()),       // yesterday 23:00
    ], now);
    expect(s.count).toBe(3);
    expect(s.hourly.length).toBe(24);
    expect(s.hourly[2]).toBe(2);
    expect(s.hourly[9]).toBe(1);
    expect(s.hourly.reduce((a, b) => a + b, 0)).toBe(3);
  });
});
