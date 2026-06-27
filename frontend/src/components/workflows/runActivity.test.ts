import { describe, expect, it } from "vitest";

import type { PhaseAgentRef, RunPhase, RunTimelineEvent } from "@/api/workflows";

import { buildWaves, composeActivity, formatDuration, formatUsd, mergeActivityStream, waveBreakdown, type AgentWave } from "./runActivity";

function agent(id: string): PhaseAgentRef {
  return { agentRunId: id, status: "Running" };
}

function event(o: Partial<RunTimelineEvent> & { id: string; occurredAt: string }): RunTimelineEvent {
  return { kind: "x", title: o.id, severity: "Info", sourceKey: "run-record", ...o };
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

describe("formatUsd", () => {
  it("renders a dollar or more with 2dp, sub-dollar with up to 4dp (trailing zeros trimmed)", () => {
    expect(formatUsd(30)).toBe("$30.00");
    expect(formatUsd(12.3)).toBe("$12.30");
    expect(formatUsd(0.0045)).toBe("$0.0045");
    expect(formatUsd(0.055)).toBe("$0.055");   // 0.0550 → trailing zero trimmed
    expect(formatUsd(0.05)).toBe("$0.05");
  });
});

describe("waveBreakdown", () => {
  it("counts agents per state (queued folds Queued, failed folds every terminal error)", () => {
    const b = waveBreakdown([agentWith("Succeeded"), agentWith("Running"), agentWith("Queued"), agentWith("TimedOut"), agentWith("Cancelled")]);
    expect(b).toEqual({ total: 5, running: 1, done: 1, queued: 1, failed: 2 });
  });
});

describe("mergeActivityStream", () => {
  const wave = (id: string, startedAt: string | null, agents: string[] = ["a1"]): AgentWave => ({ id, kind: "phase", label: id, startedAt, agents: agents.map(agent) });

  it("interleaves a wave between events by its startedAt, an event sorting before a wave on a tie", () => {
    const stream = mergeActivityStream([
      event({ id: "run-started", occurredAt: "2026-06-23T10:00:00Z" }),
      event({ id: "spawned", occurredAt: "2026-06-23T10:00:02Z" }),
      event({ id: "edited", occurredAt: "2026-06-23T10:00:09Z", agentRunId: "a1" }),
    ], [wave("impl", "2026-06-23T10:00:02Z")]);

    expect(stream.map((i) => (i.kind === "wave" ? `wave:${i.wave.id}` : i.kind === "event" ? i.event.id : "fold"))).toEqual(["run-started", "spawned", "wave:impl", "edited"]);
  });

  it("anchors a wave with no startedAt to its earliest agent event, else sends it to the end", () => {
    const anchored = mergeActivityStream([
      event({ id: "run-started", occurredAt: "2026-06-23T10:00:00Z" }),
      event({ id: "a1-edit", occurredAt: "2026-06-23T10:00:05Z", agentRunId: "a1" }),
      event({ id: "later", occurredAt: "2026-06-23T10:00:20Z" }),
    ], [wave("w", null, ["a1"])]);
    expect(anchored.map((i) => (i.kind === "event" ? i.event.id : "wave"))).toEqual(["run-started", "a1-edit", "wave", "later"]);

    const unanchored = mergeActivityStream([event({ id: "only", occurredAt: "2026-06-23T10:00:00Z" })], [wave("w", null, ["ghost"])]);
    expect(unanchored[unanchored.length - 1]).toMatchObject({ kind: "wave" });
  });
});

describe("composeActivity", () => {
  const milestone = (id: string, at: string) => event({ id, occurredAt: at, level: "Milestone" });
  const detail = (id: string, at: string) => event({ id, occurredAt: at, level: "Detail" });

  it("folds a run of two-or-more consecutive detail events; a lone detail stays inline", () => {
    const items = composeActivity([
      milestone("run-started", "2026-06-23T10:00:00Z"),
      detail("d1", "2026-06-23T10:00:01Z"),
      detail("d2", "2026-06-23T10:00:02Z"),
      milestone("mid", "2026-06-23T10:00:03Z"),
      detail("lone", "2026-06-23T10:00:04Z"),
      milestone("run-done", "2026-06-23T10:00:05Z"),
    ], []);

    expect(items.map((i) => i.kind)).toEqual(["event", "fold", "event", "event", "event"]);
  });

  it("keeps a stable fold key (anchored to the preceding item) when a detail backfills to the run's front", () => {
    const m = milestone("run-started", "2026-06-23T10:00:00Z");
    const before = composeActivity([m, detail("d2", "2026-06-23T10:00:02Z"), detail("d3", "2026-06-23T10:00:03Z")], []);
    const after = composeActivity([m, detail("d1", "2026-06-23T10:00:01Z"), detail("d2", "2026-06-23T10:00:02Z"), detail("d3", "2026-06-23T10:00:03Z")], []);

    const foldKey = (items: ReturnType<typeof composeActivity>) => items.find((i) => i.kind === "fold")?.key;
    expect(foldKey(after)).toBe(foldKey(before));
  });

  it("treats an absent level as a milestone (forward-tolerance) — never silently folds", () => {
    const items = composeActivity([event({ id: "a", occurredAt: "2026-06-23T10:00:00Z" }), event({ id: "b", occurredAt: "2026-06-23T10:00:01Z" })], []);
    expect(items.map((i) => i.kind)).toEqual(["event", "event"]);
  });
});
