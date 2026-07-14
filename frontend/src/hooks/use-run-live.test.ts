import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { RunRecordView } from "@/api/workflows";
import { useNodeLive, useRunLive } from "./use-run-live";

/**
 * `streamRunRecords` is mocked to hand back the `onRecord` callback so a test can drive the record stream synchronously.
 * The store's micro-batch is exercised under fake timers: the assertions are about batching (one flush per 80ms window),
 * per-node selection, and freezing on a terminal record.
 */
const hoisted = vi.hoisted(() => ({ onRecord: null as ((record: RunRecordView) => void) | null }));

vi.mock("@/api/run-stream", () => ({
  streamRunRecords: (_runId: string, _getAfter: () => number, onRecord: (record: RunRecordView) => void) => {
    hoisted.onRecord = onRecord;
    return new Promise<void>(() => {});
  },
}));

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

describe("useRunLive", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    hoisted.onRecord = null;
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("micro-batches: 100 deltas in one 80ms window notify subscribers at most twice", () => {
    const { result } = renderHook(() => useRunLive("run-1", true));
    const store = result.current;

    let notifications = 0;
    store.subscribe(() => (notifications += 1));

    act(() => {
      for (let i = 1; i <= 100; i += 1) hoisted.onRecord?.(rec(i, "interaction.delta", { nodeId: "n", payloadJson: JSON.stringify({ text: "x" }) }));
    });

    act(() => vi.advanceTimersByTime(80));

    expect(notifications).toBeLessThanOrEqual(2);
    expect(notifications).toBeGreaterThanOrEqual(1);
    expect(store.getState().byNode.get("n")?.stream).toEqual({ chars: 100, deltas: 100, streaming: true });
  });

  it("useNodeLive returns the folded signal for its node after a flush", () => {
    const { result } = renderHook(() => {
      const store = useRunLive("run-1", true);
      const signals = useNodeLive(store, "n");
      return { store, signals };
    });

    act(() => {
      hoisted.onRecord?.(rec(1, "interaction.delta", { nodeId: "n", payloadJson: JSON.stringify({ text: "hello" }) }));
    });
    act(() => vi.advanceTimersByTime(80));

    expect(result.current.signals?.stream).toEqual({ chars: 5, deltas: 1, streaming: true });
  });

  it("freezes on a terminal record — later records do not notify", () => {
    const { result } = renderHook(() => useRunLive("run-1", true));
    const store = result.current;

    let notifications = 0;
    store.subscribe(() => (notifications += 1));

    act(() => hoisted.onRecord?.(rec(1, "run.completed")));
    act(() => vi.advanceTimersByTime(80));

    const afterTerminal = notifications;
    expect(store.getState().terminal).toBe(true);

    act(() => hoisted.onRecord?.(rec(2, "interaction.delta", { nodeId: "n", payloadJson: JSON.stringify({ text: "late" }) })));
    act(() => vi.advanceTimersByTime(80));

    expect(notifications).toBe(afterTerminal);
    expect(store.getState().byNode.has("n")).toBe(false);
  });
});
