import { describe, expect, it } from "vitest";

import type { PhaseAgentRef, RunPhase } from "@/api/workflows";

import { buildWaves, formatDuration, waveBreakdown } from "./runActivity";

function agent(id: string): PhaseAgentRef {
  return { agentRunId: id, status: "Running" };
}

function agentWith(status: string): PhaseAgentRef {
  return { agentRunId: status, status };
}

function phase(o: Partial<RunPhase> & { id: string }): RunPhase {
  return {
    label: o.id, kind: "node", status: "Active", order: 0, agents: [],
    metrics: { agentCount: 0, succeededCount: 0, failedCount: 0 }, sourceKey: "node-summary",
    ...o,
  };
}

describe("buildWaves", () => {
  it("makes one wave per agent-bearing phase for a node / map run", () => {
    const waves = buildWaves([
      phase({ id: "code", label: "code", agents: [agent("a1")] }),
      phase({ id: "map", label: "Fan out", kind: "map", order: 1, agents: [agent("a2"), agent("a3")] }),
      phase({ id: "end", label: "end", order: 2 }),   // no agents → no wave
    ]);

    expect(waves.map((w) => w.id)).toEqual(["code", "map"]);
    expect(waves[1].agents.map((a) => a.agentRunId)).toEqual(["a2", "a3"]);
  });

  it("lets an authored 'phase' own agents the spawn decision also lists (no double wave)", () => {
    const waves = buildWaves([
      phase({ id: "decision-2", label: "Spawn 2 agents", kind: "spawn", order: 1_000_002, sourceKey: "supervisor-ledger", agents: [agent("a1"), agent("a2")] }),
      phase({ id: "phase-impl", label: "Implement", kind: "phase", order: 2_000_000, sourceKey: "supervisor-ledger", agents: [agent("a1"), agent("a2")] }),
    ]);

    expect(waves.map((w) => w.id)).toEqual(["phase-impl"]);
    expect(waves[0].agents.map((a) => a.agentRunId)).toEqual(["a1", "a2"]);
  });

  it("keeps a decision phase's agents that no authored phase claimed", () => {
    const waves = buildWaves([
      phase({ id: "decision-2", label: "Spawn 3", kind: "spawn", order: 1_000_002, agents: [agent("a1"), agent("a2"), agent("a3")] }),
      phase({ id: "phase-impl", label: "Implement", kind: "phase", order: 2_000_000, agents: [agent("a1")] }),
    ]);

    const byId = Object.fromEntries(waves.map((w) => [w.id, w.agents.map((a) => a.agentRunId)]));
    expect(byId["phase-impl"]).toEqual(["a1"]);
    expect(byId["decision-2"]).toEqual(["a2", "a3"]);
  });

  it("assigns a contested agent (same rank) to the earliest-order phase", () => {
    const waves = buildWaves([
      phase({ id: "p-late", kind: "node", order: 5, agents: [agent("a1")] }),
      phase({ id: "p-early", kind: "node", order: 1, agents: [agent("a1")] }),
    ]);

    const byId = Object.fromEntries(waves.map((w) => [w.id, w.agents.length]));
    expect(byId["p-early"]).toBe(1);
    expect(byId["p-late"]).toBeUndefined();
  });

  it("returns no waves for an agentless run (pure structural workflow)", () => {
    expect(buildWaves([phase({ id: "a" }), phase({ id: "b", order: 1 })])).toEqual([]);
  });
});

describe("formatDuration", () => {
  it("renders sub-minute as seconds, minutes as 'Nm Ns', hours as 'Nh Nm'", () => {
    expect(formatDuration(45_000)).toBe("45s");
    expect(formatDuration(137_000)).toBe("2m 17s");
    expect(formatDuration(3_780_000)).toBe("1h 3m");
    expect(formatDuration(800)).toBe("0s");
  });

  it("renders an unknown duration as an em dash", () => {
    expect(formatDuration(null)).toBe("—");
    expect(formatDuration(undefined)).toBe("—");
  });
});

describe("waveBreakdown", () => {
  it("counts agents per state (queued folds Queued, failed folds every terminal error)", () => {
    const b = waveBreakdown([agentWith("Succeeded"), agentWith("Running"), agentWith("Queued"), agentWith("TimedOut"), agentWith("Cancelled")]);
    expect(b).toEqual({ total: 5, running: 1, done: 1, queued: 1, failed: 2 });
  });
});
