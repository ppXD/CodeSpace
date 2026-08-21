import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { InvalidWorkflowRunRecordPageError, type RunRecordPageRequest, type RunRecordPageResponse, type RunRecordView, type WorkflowRunStatus } from "@/api/workflows";

const { getPage } = vi.hoisted(() => ({ getPage: vi.fn() }));
vi.mock("@/api/workflows", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/workflows")>();
  return { ...original, workflowsApi: { ...original.workflowsApi, getRunRecordPage: getPage } };
});

import { RUN_RECORD_PAGE_LIMIT, RUN_RECORD_WINDOW_LIMIT, RUN_RECORD_WINDOW_POLL_MS, useRunRecordWindow } from "./use-workflows";

function record(sequence: number): RunRecordView {
  return { sequence, recordType: `record.${sequence}`, nodeId: null, iterationKey: "", occurredAt: "2026-08-21T00:00:00Z", payloadJson: `{"sequence":${sequence}}`, correlationId: null, parentRecordId: null };
}

function records(from: number, count: number): RunRecordView[] {
  return Array.from({ length: count }, (_, index) => record(from + index));
}

function page(mode: RunRecordPageResponse["mode"], rows: RunRecordView[], status: WorkflowRunStatus = "Running", more = false): RunRecordPageResponse {
  return {
    runId: "run-1",
    runStatus: status,
    mode,
    records: rows,
    nextBeforeSequence: mode !== "Newer" && more ? rows[0]?.sequence ?? null : null,
    nextAfterSequence: mode === "Newer" && more ? rows.at(-1)?.sequence ?? null : null,
  };
}

beforeEach(() => {
  vi.useFakeTimers();
  getPage.mockReset();
});

afterEach(() => vi.useRealTimers());

async function settle() {
  await act(async () => { await Promise.resolve(); });
}

describe("useRunRecordWindow", () => {
  it("starts with one bounded Tail request and keeps raw rows outside React Query's global cache", async () => {
    getPage.mockResolvedValue(page("Tail", records(1, 2)));

    const { result } = renderHook(() => useRunRecordWindow("run-1"));
    await settle();

    expect(getPage).toHaveBeenCalledExactlyOnceWith("run-1", { limit: RUN_RECORD_PAGE_LIMIT }, expect.any(AbortSignal));
    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([1, 2]);
    expect(result.current.atLatest).toBe(true);
  });

  it("loads Older only on demand, caps the window, and explicitly marks discarded newer rows", async () => {
    getPage.mockResolvedValueOnce(page("Tail", records(513, RUN_RECORD_PAGE_LIMIT), "Running", true));
    for (const start of [385, 257, 129, 1])
      getPage.mockResolvedValueOnce(page("Older", records(start, RUN_RECORD_PAGE_LIMIT), "Running", start > 1));

    const { result } = renderHook(() => useRunRecordWindow("run-1"));
    await settle();
    expect(result.current.records).toHaveLength(RUN_RECORD_PAGE_LIMIT);
    for (let i = 0; i < 4; i += 1) {
      await act(async () => result.current.loadOlder());
      expect(result.current.isLoadingOlder).toBe(false);
    }

    expect(result.current.records).toHaveLength(RUN_RECORD_WINDOW_LIMIT);
    expect(result.current.records[0].sequence).toBe(1);
    expect(result.current.records.at(-1)?.sequence).toBe(RUN_RECORD_WINDOW_LIMIT);
    expect(result.current.atLatest).toBe(false);
    expect(result.current.newerRecordsOmitted).toBe(true);
    expect(getPage.mock.calls.slice(1).map((call) => (call[1] as RunRecordPageRequest).beforeSequence)).toEqual([513, 385, 257, 129]);
  });

  it("polls one bounded Newer page only while active/latest and stops on a terminal response", async () => {
    getPage
      .mockResolvedValueOnce(page("Tail", records(1, 2)))
      .mockResolvedValueOnce(page("Newer", records(3, 2), "Success"));

    const { result } = renderHook(() => useRunRecordWindow("run-1"));
    await settle();
    expect(result.current.records).toHaveLength(2);
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS));
    expect(result.current.records).toHaveLength(4);
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS * 2));

    expect(getPage).toHaveBeenCalledTimes(2);
    expect(getPage.mock.calls[1][1]).toEqual({ afterSequence: 2, limit: RUN_RECORD_PAGE_LIMIT });
    expect(result.current.runStatus).toBe("Success");
  });

  it("continues bounded polling after an empty healthy Newer page", async () => {
    getPage
      .mockResolvedValueOnce(page("Tail", [record(1)]))
      .mockResolvedValueOnce(page("Newer", []))
      .mockResolvedValueOnce(page("Newer", [record(2)], "Success"));

    const { result } = renderHook(() => useRunRecordWindow("run-1"));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS));
    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([1]);
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS));

    expect(getPage).toHaveBeenCalledTimes(3);
    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([1, 2]);
  });

  it("caps repeated Newer pages and exposes the discarded older side of the window", async () => {
    getPage.mockResolvedValueOnce(page("Tail", records(1, RUN_RECORD_PAGE_LIMIT)));
    for (const start of [129, 257, 385])
      getPage.mockResolvedValueOnce(page("Newer", records(start, RUN_RECORD_PAGE_LIMIT)));
    getPage.mockResolvedValueOnce(page("Newer", records(513, RUN_RECORD_PAGE_LIMIT), "Success"));

    const { result } = renderHook(() => useRunRecordWindow("run-1"));
    await settle();
    for (let i = 0; i < 4; i += 1)
      await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS));

    expect(result.current.records).toHaveLength(RUN_RECORD_WINDOW_LIMIT);
    expect(result.current.records[0].sequence).toBe(129);
    expect(result.current.records.at(-1)?.sequence).toBe(640);
    expect(result.current.olderRecordsOmitted).toBe(true);
    expect(result.current.hasOlder).toBe(true);
  });

  it("keeps the last valid window and fail-closes a contradictory page", async () => {
    getPage.mockResolvedValueOnce(page("Tail", records(1, 2))).mockRejectedValueOnce(new InvalidWorkflowRunRecordPageError());

    const { result } = renderHook(() => useRunRecordWindow("run-1"));
    await settle();
    expect(result.current.records).toHaveLength(2);
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS));
    expect(result.current.error).toBeInstanceOf(InvalidWorkflowRunRecordPageError);
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS * 2));

    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([1, 2]);
    expect(getPage).toHaveBeenCalledTimes(2);
  });

  it("retains stale rows and retries a transient Newer fault with bounded backoff before terminal stop", async () => {
    getPage
      .mockResolvedValueOnce(page("Tail", records(1, 2)))
      .mockRejectedValueOnce(new Error("backend unavailable"))
      .mockResolvedValueOnce(page("Newer", [record(3)]))
      .mockResolvedValueOnce(page("Newer", [record(4)], "Success"));

    const { result } = renderHook(() => useRunRecordWindow("run-1"));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS));

    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([1, 2]);
    expect(result.current.error).toEqual(new Error("backend unavailable"));
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS * 2 - 1));
    expect(getPage).toHaveBeenCalledTimes(2);
    await act(() => vi.advanceTimersByTimeAsync(1));

    expect(getPage).toHaveBeenCalledTimes(3);
    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([1, 2, 3]);
    expect(result.current.error).toBeNull();
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS - 1));
    expect(getPage).toHaveBeenCalledTimes(3);
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(getPage).toHaveBeenCalledTimes(4);
    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([1, 2, 3, 4]);
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS * 8));
    expect(getPage).toHaveBeenCalledTimes(4);
  });

  it("aborts in-flight work on close/unmount/run switch and ignores stale replies", async () => {
    let resolveFirst!: (value: RunRecordPageResponse) => void;
    getPage
      .mockImplementationOnce((_runId: string, _request: RunRecordPageRequest, signal: AbortSignal) => new Promise<RunRecordPageResponse>((resolve) => {
        expect(signal.aborted).toBe(false);
        resolveFirst = resolve;
      }))
      .mockResolvedValueOnce({ ...page("Tail", [record(20)]), runId: "run-2" });

    const { result, rerender, unmount } = renderHook(({ runId, open }) => useRunRecordWindow(open ? runId : null), { initialProps: { runId: "run-1", open: true } });
    await settle();
    expect(getPage).toHaveBeenCalledTimes(1);
    const firstSignal = getPage.mock.calls[0][2] as AbortSignal;

    rerender({ runId: "run-2", open: true });
    expect(firstSignal.aborted).toBe(true);
    await settle();
    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([20]);
    await act(async () => resolveFirst(page("Tail", [record(1)])));
    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([20]);

    const secondSignal = getPage.mock.calls[1][2] as AbortSignal;
    unmount();
    expect(secondSignal.aborted).toBe(true);
  });

  it("aborts an in-flight Newer poll when the Trace surface unmounts", async () => {
    getPage
      .mockResolvedValueOnce(page("Tail", [record(1)]))
      .mockImplementationOnce(() => new Promise<RunRecordPageResponse>(() => {}));

    const { unmount } = renderHook(() => useRunRecordWindow("run-1"));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(RUN_RECORD_WINDOW_POLL_MS));
    const pollSignal = getPage.mock.calls[1][2] as AbortSignal;
    expect(pollSignal.aborted).toBe(false);

    unmount();

    expect(pollSignal.aborted).toBe(true);
  });

  it("returns a historical window to a fresh Tail instead of splicing incomparable pages", async () => {
    getPage
      .mockResolvedValueOnce(page("Tail", records(513, RUN_RECORD_PAGE_LIMIT), "Running", true));
    for (const start of [385, 257, 129, 1])
      getPage.mockResolvedValueOnce(page("Older", records(start, RUN_RECORD_PAGE_LIMIT), "Running", start > 1));
    getPage.mockResolvedValueOnce(page("Tail", records(900, 2)));

    const { result } = renderHook(() => useRunRecordWindow("run-1"));
    await settle();
    expect(result.current.records).toHaveLength(RUN_RECORD_PAGE_LIMIT);
    for (let i = 0; i < 4; i += 1) {
      await act(async () => result.current.loadOlder());
      expect(result.current.isLoadingOlder).toBe(false);
    }
    expect(result.current.atLatest).toBe(false);

    await act(async () => result.current.returnToLatest());
    await settle();
    expect(result.current.records.map(({ sequence }) => sequence)).toEqual([900, 901]);
    expect(getPage.mock.calls.at(-1)?.[1]).toEqual({ limit: RUN_RECORD_PAGE_LIMIT });
    expect(result.current.atLatest).toBe(true);
  });
});
