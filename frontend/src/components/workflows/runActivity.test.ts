import { describe, expect, it } from "vitest";

import type { PhaseAgentRef, RunPhase, RunTimelineEvent } from "@/api/workflows";

import { buildWaves, formatDuration, mergeActivityStream, waveSummary, type AgentWave } from "./runActivity";

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

function event(o: Partial<RunTimelineEvent> & { id: string; occurredAt: string }): RunTimelineEvent {
  return { kind: "x", title: o.id, severity: "Info", sourceKey: "run-record", ...o };
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
    // A supervisor run: the spawn decision (rank 0) AND the authored Implement phase (rank 1) both list a1+a2.
    const waves = buildWaves([
      phase({ id: "decision-2", label: "Spawn 2 agents", kind: "spawn", order: 1_000_002, sourceKey: "supervisor-ledger", agents: [agent("a1"), agent("a2")] }),
      phase({ id: "phase-impl", label: "Implement", kind: "phase", order: 2_000_000, sourceKey: "supervisor-ledger", agents: [agent("a1"), agent("a2")] }),
    ]);

    // The authored phase wins; the spawn decision contributes no wave (its agents were claimed).
    expect(waves.map((w) => w.id)).toEqual(["phase-impl"]);
    expect(waves[0].agents.map((a) => a.agentRunId)).toEqual(["a1", "a2"]);
  });

  it("keeps a decision phase's agents that no authored phase claimed", () => {
    const waves = buildWaves([
      phase({ id: "decision-2", label: "Spawn 3", kind: "spawn", order: 1_000_002, agents: [agent("a1"), agent("a2"), agent("a3")] }),
      phase({ id: "phase-impl", label: "Implement", kind: "phase", order: 2_000_000, agents: [agent("a1")] }),   // claims only a1
    ]);

    const byId = Object.fromEntries(waves.map((w) => [w.id, w.agents.map((a) => a.agentRunId)]));
    expect(byId["phase-impl"]).toEqual(["a1"]);
    // a2/a3 stay with the spawn since no authored phase took them.
    expect(byId["decision-2"]).toEqual(["a2", "a3"]);
  });

  it("assigns a contested agent (same rank) to the earliest-order phase", () => {
    const waves = buildWaves([
      phase({ id: "p-late", kind: "node", order: 5, agents: [agent("a1")] }),
      phase({ id: "p-early", kind: "node", order: 1, agents: [agent("a1")] }),
    ]);

    const byId = Object.fromEntries(waves.map((w) => [w.id, w.agents.length]));
    expect(byId["p-early"]).toBe(1);
    expect(byId["p-late"]).toBeUndefined();   // lost the contest → no wave
  });

  it("returns no waves for an agentless run (pure structural workflow)", () => {
    expect(buildWaves([phase({ id: "a" }), phase({ id: "b", order: 1 })])).toEqual([]);
  });
});

describe("mergeActivityStream", () => {
  const wave = (id: string, startedAt: string | null, agents: string[] = ["a1"]): AgentWave => ({ id, label: id, startedAt, agents: agents.map(agent) });

  it("interleaves a wave between the events by its startedAt", () => {
    const events = [
      event({ id: "run-started", occurredAt: "2026-06-23T10:00:00Z" }),
      event({ id: "spawned", occurredAt: "2026-06-23T10:00:02Z" }),
      event({ id: "edited", occurredAt: "2026-06-23T10:00:09Z", agentRunId: "a1" }),
    ];
    const waves = [wave("impl", "2026-06-23T10:00:02Z")];

    const stream = mergeActivityStream(events, waves);

    expect(stream.map((i) => (i.kind === "event" ? i.event.id : `wave:${i.wave.id}`)))
      .toEqual(["run-started", "spawned", "wave:impl", "edited"]);
  });

  it("sorts an event BEFORE a wave at the same timestamp (wave lands after its spawn announcement)", () => {
    const t = "2026-06-23T10:00:02Z";
    const stream = mergeActivityStream([event({ id: "spawned", occurredAt: t })], [wave("impl", t)]);

    expect(stream.map((i) => i.kind)).toEqual(["event", "wave"]);
  });

  it("falls back to the earliest agent-event time when a wave has no startedAt", () => {
    const events = [
      event({ id: "run-started", occurredAt: "2026-06-23T10:00:00Z" }),
      event({ id: "a1-edit", occurredAt: "2026-06-23T10:00:05Z", agentRunId: "a1" }),
      event({ id: "later", occurredAt: "2026-06-23T10:00:20Z" }),
    ];
    const stream = mergeActivityStream(events, [wave("w", null, ["a1"])]);

    // anchored at a1's first event (10:00:05) → between run-started and later.
    expect(stream.map((i) => (i.kind === "event" ? i.event.id : "wave"))).toEqual(["run-started", "a1-edit", "wave", "later"]);
  });

  it("sends an unanchored wave (no startedAt, no agent events) to the end, never the top", () => {
    const events = [event({ id: "only", occurredAt: "2026-06-23T10:00:00Z" })];
    const stream = mergeActivityStream(events, [wave("w", null, ["ghost"])]);

    expect(stream[stream.length - 1]).toMatchObject({ kind: "wave" });
  });

  it("is deterministic — equal-time events keep their input order", () => {
    const t = "2026-06-23T10:00:00Z";
    const stream = mergeActivityStream([event({ id: "first", occurredAt: t }), event({ id: "second", occurredAt: t })], []);

    expect(stream.map((i) => (i.kind === "event" ? i.event.id : ""))).toEqual(["first", "second"]);
  });
});

describe("formatDuration", () => {
  it("renders sub-minute as seconds, minutes as 'Nm Ns', hours as 'Nh Nm'", () => {
    expect(formatDuration(45_000)).toBe("45s");
    expect(formatDuration(137_000)).toBe("2m 17s");   // the table's running example
    expect(formatDuration(3_780_000)).toBe("1h 3m");
    expect(formatDuration(800)).toBe("0s");           // sub-second floors, never blank
  });

  it("renders an unknown duration as an em dash", () => {
    expect(formatDuration(null)).toBe("—");
    expect(formatDuration(undefined)).toBe("—");
  });
});

describe("waveSummary", () => {
  it("counts the done agents and totals the wave", () => {
    const s = waveSummary([agentWith("Succeeded"), agentWith("Running"), agentWith("Succeeded")]);
    expect(s.done).toBe(2);
    expect(s.total).toBe(3);
  });

  it("aggregates the headline state: running wins, then failed, then waiting, else done", () => {
    expect(waveSummary([agentWith("Succeeded"), agentWith("Running")]).state).toBe("running");
    expect(waveSummary([agentWith("Succeeded"), agentWith("Failed")]).state).toBe("failed");
    expect(waveSummary([agentWith("Succeeded"), agentWith("Queued")]).state).toBe("waiting");
    expect(waveSummary([agentWith("Succeeded"), agentWith("Succeeded")]).state).toBe("done");
  });
});
