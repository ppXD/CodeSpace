import { describe, expect, it } from "vitest";

import type { RunPhase } from "@/api/workflows";

import { isAgentBusy, summarizeRunState } from "./runPhases";

/** A bare phase with sensible defaults; override per case. */
function phase(o: Partial<RunPhase>): RunPhase {
  return {
    id: "p", label: "P", kind: "node", status: "Pending", order: 0,
    agents: [], metrics: { agentCount: 0, succeededCount: 0, failedCount: 0 }, sourceKey: "s",
    ...o,
  };
}

describe("isAgentBusy", () => {
  it("is true only while an agent is still working", () => {
    expect(isAgentBusy("Running")).toBe(true);
    expect(isAgentBusy("Queued")).toBe(true);
    expect(isAgentBusy("Succeeded")).toBe(false);
    expect(isAgentBusy("Failed")).toBe(false);
  });
});

describe("summarizeRunState", () => {
  it("is empty-but-safe for a run with no phases", () => {
    expect(summarizeRunState("Running", [])).toEqual({ lead: "Running", focus: "", activeAgents: 0, totalAgents: 0, waiting: 0 });
  });

  it("focuses the active phase and tallies busy / total agents", () => {
    const s = summarizeRunState("Running", [
      phase({ id: "plan", label: "Plan", status: "Succeeded", metrics: { agentCount: 1, succeededCount: 1, failedCount: 0 }, agents: [{ agentRunId: "a1", status: "Succeeded" }] }),
      phase({ id: "impl", label: "Implement", status: "Active", metrics: { agentCount: 3, succeededCount: 0, failedCount: 0 }, agents: [
        { agentRunId: "a2", status: "Running" }, { agentRunId: "a3", status: "Running" }, { agentRunId: "a4", status: "Queued" },
      ] }),
    ]);
    expect(s.lead).toBe("Running");
    expect(s.focus).toBe("Implement");   // the Active phase
    expect(s.totalAgents).toBe(4);        // 1 + 3
    expect(s.activeAgents).toBe(3);       // 2 Running + 1 Queued; the Succeeded one is not busy
    expect(s.waiting).toBe(0);
  });

  it("counts each agent once when phases overlap (a phased supervisor lists an agent twice)", () => {
    // The supervisor source emits the same agentRunId in BOTH its spawn-decision phase and its
    // model-authored semantic phase — summing per-phase metrics would double-count to "4 of 4".
    const a1 = { agentRunId: "agent-A", status: "Running" };
    const a2 = { agentRunId: "agent-B", status: "Running" };
    const s = summarizeRunState("Running", [
      phase({ id: "decision-2", label: "Spawn", status: "Active", metrics: { agentCount: 2, succeededCount: 0, failedCount: 0 }, agents: [a1, a2] }),
      phase({ id: "phase-impl", label: "Implement", status: "Active", metrics: { agentCount: 1, succeededCount: 0, failedCount: 0 }, agents: [a1] }),
      phase({ id: "phase-verify", label: "Verify", status: "Pending", metrics: { agentCount: 1, succeededCount: 0, failedCount: 0 }, agents: [a2] }),
    ]);
    expect(s.totalAgents).toBe(2);    // agent-A + agent-B, not 2+1+1
    expect(s.activeAgents).toBe(2);   // both Running, counted once each
  });

  it("falls back to a waiting phase for focus and counts waiting", () => {
    const s = summarizeRunState("Suspended", [
      phase({ id: "n1", label: "Fetch", status: "Succeeded" }),
      phase({ id: "appr", label: "Approve push", status: "Waiting" }),
    ]);
    expect(s.focus).toBe("Approve push");
    expect(s.waiting).toBe(1);
  });
});
