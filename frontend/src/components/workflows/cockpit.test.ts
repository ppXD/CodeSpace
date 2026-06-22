import { describe, expect, it } from "vitest";

import type { PendingDecision, WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";

import { compactAge, countRuns, summarizeDecisions, summarizeToday } from "./cockpit";

function decision(o: Partial<PendingDecision>): PendingDecision {
  return {
    id: "d", grain: "tool_ledger", rootTraceId: "r", decisionType: "confirm", question: "?",
    options: [], riskLevel: "low", policy: "human_required", createdAt: "2026-06-22T00:00:00Z", ...o,
  };
}

function run(id: string, status: WorkflowRunStatus, createdDate = "2026-06-22T00:00:00Z"): WorkflowRunSummary {
  return { id, workflowId: "w", workflowVersion: 1, sourceType: "manual", status, error: null, startedAt: null, completedAt: null, createdDate };
}

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

describe("countRuns", () => {
  it("buckets by status (live = pending/enqueued/running)", () => {
    const c = countRuns([
      run("1", "Running"), run("2", "Pending"), run("3", "Enqueued"),
      run("4", "Failure"), run("5", "Suspended"), run("6", "Success"), run("7", "Cancelled"),
    ]);
    expect(c).toEqual({ live: 3, failed: 1, suspended: 1 });
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
