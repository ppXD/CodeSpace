import { describe, expect, it } from "vitest";

import type { PendingDecision } from "@/api/workflows";

import { deadlineLabel, decisionsForRun, isSingleChoice } from "./runDecisions";

function decision(o: Partial<PendingDecision>): PendingDecision {
  return {
    id: "d", grain: "tool_ledger", rootTraceId: "run-1", decisionType: "confirm", question: "?",
    options: [], riskLevel: "low", policy: "human_required", createdAt: "2026-06-22T00:00:00Z", ...o,
  };
}

describe("decisionsForRun", () => {
  it("keeps only the decisions whose trace root is this run", () => {
    const all = [decision({ id: "a", rootTraceId: "run-1" }), decision({ id: "b", rootTraceId: "run-2" }), decision({ id: "c", rootTraceId: "run-1" })];
    expect(decisionsForRun(all, "run-1").map((d) => d.id)).toEqual(["a", "c"]);
  });

  it("is empty when none belong to the run", () => {
    expect(decisionsForRun([decision({ rootTraceId: "other" })], "run-1")).toEqual([]);
  });

  it("also matches an agent-grain decision by its agent run id (its rootTraceId is the agent's, not the run's)", () => {
    const agentDecision = decision({ id: "ag", rootTraceId: "agent-9", agentRunId: "agent-9" });
    const otherAgent = decision({ id: "x", rootTraceId: "agent-7", agentRunId: "agent-7" });
    const out = decisionsForRun([agentDecision, otherAgent], "run-1", new Set(["agent-9"]));
    expect(out.map((d) => d.id)).toEqual(["ag"]);   // agent-9 is one of this run's agents; agent-7 isn't
  });
});

describe("isSingleChoice", () => {
  it("is true for the one-click shapes and false for compose-then-submit shapes", () => {
    expect(isSingleChoice("confirm")).toBe(true);
    expect(isSingleChoice("choose_one")).toBe(true);
    expect(isSingleChoice("approve_action")).toBe(true);
    expect(isSingleChoice("choose_many")).toBe(false);
    expect(isSingleChoice("free_text")).toBe(false);
    expect(isSingleChoice("some_future_type")).toBe(false);   // unknown → free-text answer, not single-click
  });
});

describe("deadlineLabel", () => {
  const now = 1_700_000_000_000;

  it("is null without a deadline", () => {
    expect(deadlineLabel(null, now)).toBeNull();
    expect(deadlineLabel(undefined, now)).toBeNull();
  });

  it("counts down in the right unit, and reads 'due now' once past", () => {
    expect(deadlineLabel(new Date(now + 20_000).toISOString(), now)).toBe("<1m left");   // sub-minute, not "0m left"
    expect(deadlineLabel(new Date(now + 5 * 60_000).toISOString(), now)).toBe("5m left");
    expect(deadlineLabel(new Date(now + 3 * 3_600_000).toISOString(), now)).toBe("3h left");
    expect(deadlineLabel(new Date(now + 2 * 86_400_000).toISOString(), now)).toBe("2d left");
    expect(deadlineLabel(new Date(now - 1_000).toISOString(), now)).toBe("due now");
  });
});
