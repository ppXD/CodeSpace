import { createContext, useEffect, useState, useSyncExternalStore } from "react";

import { streamRunRecords } from "@/api/run-stream";
import type { RunRecordView } from "@/api/workflows";
import { emptyRunLiveState, foldRecord } from "@/lib/runLiveFold";
import type { NodeLiveSignals, RunLiveState } from "@/lib/runLiveFold";

/**
 * A canvas-level external store fed by ONE run SSE connection: incoming ledger records are micro-batched (an 80ms window)
 * and folded into per-node live signals, so a footer subscribing to a single node re-renders only when THAT node's
 * signals move — token deltas at tens/sec never re-render the whole canvas. Read via {@link useNodeLive}.
 */
export interface RunLiveStore {
  getState(): RunLiveState;
  subscribe(cb: () => void): () => void;
}

interface InternalRunLiveStore extends RunLiveStore {
  /** Queue a record for the next 80ms flush; ignored once frozen. */
  enqueue(record: RunRecordView): void;
  /** Register the callback invoked when a terminal `run.*` record lands (the hook aborts the SSE). */
  setOnTerminal(cb: () => void): void;
}

/**
 * Micro-batching store: records accumulate in a queue drained on an 80ms timer; each flush folds the queued records and
 * notifies subscribers at most ONCE. A terminal `run.*` record flushes, notifies, then freezes the store — no further
 * enqueues or notifications, and the owner is signalled (via `onTerminal`) to close the SSE.
 */
function createRunLiveStore(): InternalRunLiveStore {
  let state = emptyRunLiveState();
  const subscribers = new Set<() => void>();

  let queue: RunRecordView[] = [];
  let timer: ReturnType<typeof setTimeout> | null = null;
  let frozen = false;
  let onTerminal: (() => void) | null = null;

  const flush = () => {
    timer = null;
    if (frozen || queue.length === 0) {
      queue = [];
      return;
    }

    const batch = queue;
    queue = [];

    let next = state;
    let terminalHit = false;
    for (const record of batch) {
      next = foldRecord(next, record);
      if (next.terminal) terminalHit = true;
    }

    const changed = next !== state;
    state = next;

    if (changed) for (const cb of subscribers) cb();

    if (terminalHit) {
      frozen = true;
      onTerminal?.();
    }
  };

  return {
    getState: () => state,
    subscribe(cb) {
      subscribers.add(cb);
      return () => subscribers.delete(cb);
    },
    enqueue(record) {
      if (frozen) return;
      queue.push(record);
      if (timer === null) timer = setTimeout(flush, 80);
    },
    setOnTerminal(cb) {
      onTerminal = cb;
    },
  };
}

/**
 * Own a {@link RunLiveStore} for `runId`, opening the SSE tail while `enabled`. The store is stable across renders (and
 * reset when `runId` changes); the SSE opens on an AbortController torn down on cleanup / when disabled / on a terminal
 * record. When disabled or the stream errors the store simply stays empty — consumers degrade to their poll data.
 */
export function useRunLive(runId: string, enabled: boolean): RunLiveStore {
  const [store, setStore] = useState(createRunLiveStore);
  const [storeRunId, setStoreRunId] = useState(runId);

  // Reset the store when runId changes (React's "adjust state during render" pattern): the new store's SSE opens in the
  // effect below, keyed on the new runId + store.
  if (runId !== storeRunId) {
    setStoreRunId(runId);
    setStore(createRunLiveStore());
  }

  useEffect(() => {
    if (!enabled) return;

    const controller = new AbortController();
    let cursor = 0;

    store.setOnTerminal(() => controller.abort());

    const onRecord = (record: RunRecordView) => {
      if (record.sequence > cursor) cursor = record.sequence;
      store.enqueue(record);
    };

    void streamRunRecords(runId, () => cursor, onRecord, controller.signal);

    return () => controller.abort();
  }, [runId, enabled, store]);

  return store;
}

/**
 * Subscribe a single node's live signals: re-renders only when node `nodeId`'s signals object changes reference. Because
 * {@link foldRecord} keeps every untouched node's object identity across states, a footer for node X skips the render
 * churn of every other node's token deltas. Returns null while the node has no live signals yet.
 */
export function useNodeLive(store: RunLiveStore, nodeId: string): NodeLiveSignals | null {
  return useSyncExternalStore(store.subscribe, () => store.getState().byNode.get(nodeId) ?? null);
}

/** Canvas-scoped store handle for later footer consumers; the mount point lands with the first footer PR. */
export const RunLiveContext = createContext<RunLiveStore | null>(null);
