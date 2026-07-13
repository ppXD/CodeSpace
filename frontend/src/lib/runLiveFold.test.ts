import { describe, expect, it } from "vitest";

import type { RunRecordView } from "@/api/workflows";
import { emptyRunLiveState, foldRecord } from "./runLiveFold";

/**
 * The SSE subscription + micro-batch live in {@link useRunLive}; the load-bearing, testable core is this pure fold that
 * turns a run's raw ledger tail into O(1) per-node live signals — counts and in-flight markers, never accumulated text.
 */
function rec(sequence: number, recordType: string, over: Partial<RunRecordView> = {}): RunRecordView {
  return {
    sequence,
    recordType,
    nodeId: null,
    iterationKey: "",
    occurredAt: "2026-07-08T00:00:00Z",
    payloadJson: "{}",
    correlationId: null,
    parentRecordId: null,
    ...over,
  };
}

/** Fold an ordered list of records from empty and return the final state. */
function replay(records: RunRecordView[]) {
  return records.reduce(foldRecord, emptyRunLiveState());
}

describe("foldRecord", () => {
  it("folds an ordered fixture into per-node signals", () => {
    const state = replay([
      rec(1, "external_call.started", { nodeId: "call", payloadJson: JSON.stringify({ target: "github", method: "POST" }) }),
      rec(2, "interaction.delta", { nodeId: "stream", payloadJson: JSON.stringify({ text: "hel" }) }),
      rec(3, "interaction.delta", { nodeId: "stream", payloadJson: JSON.stringify({ text: "lo" }) }),
      rec(4, "interaction.completed", { nodeId: "stream" }),
      rec(5, "external_call.completed", { nodeId: "call" }),
      rec(6, "node.suspended", { nodeId: "wait", iterationKey: "b0", payloadJson: JSON.stringify({ waitKind: "human", deadline: "2026-07-08T01:00:00Z" }) }),
      rec(7, "node.started", { nodeId: "fan", iterationKey: "i0" }),
      rec(8, "node.started", { nodeId: "fan", iterationKey: "i1" }),
      rec(9, "node.completed", { nodeId: "fan", iterationKey: "i0" }),
    ]);

    expect(state.byNode.get("call")?.call).toBeUndefined();

    const stream = state.byNode.get("stream")?.stream;
    expect(stream).toEqual({ chars: 5, deltas: 2, streaming: false });

    const wait = state.byNode.get("wait")?.wait;
    expect(wait?.kind).toBe("human");
    expect(wait?.sinceMs).toBe(Date.parse("2026-07-08T00:00:00Z"));
    expect(wait?.deadlineAtMs).toBe(Date.parse("2026-07-08T01:00:00Z"));

    expect(state.byNode.get("fan")?.branches).toEqual({ done: 1, failed: 0, running: 1, waiting: 0 });
  });

  it("sets then clears a call across started -> completed", () => {
    const opened = foldRecord(emptyRunLiveState(), rec(1, "external_call.started", { nodeId: "n", payloadJson: JSON.stringify({ target: "gitlab", method: "GET" }) }));
    expect(opened.byNode.get("n")?.call).toEqual({ target: "gitlab", method: "GET", startedAtMs: Date.parse("2026-07-08T00:00:00Z") });

    const closed = foldRecord(opened, rec(2, "external_call.failed", { nodeId: "n" }));
    expect(closed.byNode.get("n")?.call).toBeUndefined();
  });

  it("defaults missing external_call target/method to empty strings", () => {
    const state = foldRecord(emptyRunLiveState(), rec(1, "external_call.started", { nodeId: "n" }));
    expect(state.byNode.get("n")?.call).toEqual({ target: "", method: "", startedAtMs: Date.parse("2026-07-08T00:00:00Z") });
  });

  it("clears a node's wait on a later record of a different type", () => {
    const suspended = foldRecord(emptyRunLiveState(), rec(1, "node.suspended", { nodeId: "n", iterationKey: "b0" }));
    expect(suspended.byNode.get("n")?.wait).toBeDefined();

    const resumed = foldRecord(suspended, rec(2, "node.started", { nodeId: "n", iterationKey: "b0" }));
    expect(resumed.byNode.get("n")?.wait).toBeUndefined();
  });

  it("counts suspended branches as waiting", () => {
    const state = replay([
      rec(1, "node.started", { nodeId: "fan", iterationKey: "i0" }),
      rec(2, "node.suspended", { nodeId: "fan", iterationKey: "i1" }),
    ]);
    expect(state.byNode.get("fan")?.branches).toEqual({ done: 0, failed: 0, running: 1, waiting: 1 });
  });

  it("drops an out-of-order record (sequence <= lastSeq) and returns the same reference", () => {
    const s1 = foldRecord(emptyRunLiveState(), rec(5, "interaction.delta", { nodeId: "n", payloadJson: JSON.stringify({ text: "abc" }) }));
    const s2 = foldRecord(s1, rec(3, "interaction.delta", { nodeId: "n", payloadJson: JSON.stringify({ text: "zzz" }) }));

    expect(s2).toBe(s1);
    expect(s2.byNode.get("n")?.stream?.chars).toBe(3);
  });

  it("dedups a duplicate sequence without double-counting", () => {
    const delta = rec(5, "interaction.delta", { nodeId: "n", payloadJson: JSON.stringify({ text: "abc" }) });
    const s1 = foldRecord(emptyRunLiveState(), delta);
    const s2 = foldRecord(s1, delta);

    expect(s2).toBe(s1);
    expect(s2.byNode.get("n")?.stream).toEqual({ chars: 3, deltas: 1, streaming: true });
  });

  it("tolerates a malformed payload without throwing", () => {
    const bad = rec(1, "interaction.delta", { nodeId: "n", payloadJson: "{not json" });
    const state = foldRecord(emptyRunLiveState(), bad);

    expect(state.byNode.get("n")?.stream).toEqual({ chars: 0, deltas: 1, streaming: true });
  });

  it("marks the state terminal on a run.completed", () => {
    const state = foldRecord(emptyRunLiveState(), rec(1, "run.completed"));
    expect(state.terminal).toBe(true);
  });

  it("keeps an untouched node's signals object reference-equal across a fold that changed another node", () => {
    const withA = foldRecord(emptyRunLiveState(), rec(1, "interaction.delta", { nodeId: "a", payloadJson: JSON.stringify({ text: "x" }) }));
    const withB = foldRecord(withA, rec(2, "interaction.delta", { nodeId: "b", payloadJson: JSON.stringify({ text: "y" }) }));

    const bBefore = withB.byNode.get("b");

    const changedA = foldRecord(withB, rec(3, "interaction.delta", { nodeId: "a", payloadJson: JSON.stringify({ text: "z" }) }));

    expect(changedA).not.toBe(withB);
    expect(changedA.byNode.get("b")).toBe(bBefore);
    expect(changedA.byNode.get("a")).not.toBe(withB.byNode.get("a"));
  });

  it("returns the same reference for an unknown record type with no wait to clear", () => {
    const s0 = emptyRunLiveState();
    const s1 = foldRecord(s0, rec(1, "log", { nodeId: "n" }));

    expect(s1).toBe(s0);
    expect(s1.byNode.has("n")).toBe(false);
  });
});
