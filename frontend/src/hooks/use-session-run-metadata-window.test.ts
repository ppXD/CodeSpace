import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/api/request";
import { InvalidSessionRunMetadataPageError, type SessionRunMetadataItem, type SessionRunMetadataPage, type SessionRunMetadataSelector } from "@/api/sessions";

const { getPage } = vi.hoisted(() => ({ getPage: vi.fn() }));
vi.mock("@/api/sessions", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/sessions")>();
  return { ...original, sessionsApi: { ...original.sessionsApi, pageRunMetadata: getPage } };
});

import { SESSION_RUN_METADATA_PAGE_LIMIT, SESSION_RUN_METADATA_POLL_MS, SESSION_RUN_METADATA_WINDOW_LIMIT, useSessionRunMetadataWindow } from "./use-session-run-metadata-window";

const selector: SessionRunMetadataSelector = { kind: "Session", sessionId: "11111111-1111-1111-1111-111111111111", runAnchorId: null };
const anchorRunId = "22222222-2222-2222-2222-222222222222";

function item(runNumber: number): SessionRunMetadataItem {
  return {
    runId: `00000000-0000-0000-0000-${String(runNumber).padStart(12, "0")}`,
    runNumber,
    runRequestId: "33333333-3333-3333-3333-333333333333",
    rootRunId: null,
    sessionTurnIndex: runNumber,
    status: "Running",
    projectionKind: { text: null, sizeBytes: 0, state: "None" },
    sourceType: { text: "snapshot", sizeBytes: 8, state: "Complete" },
    rerunFromNodeId: { text: null, sizeBytes: 0, state: "None" },
    createdDate: "2026-08-21T00:00:00Z",
    startedAt: null,
    completedAt: null,
    error: { text: null, sizeBytes: 0, state: "None" },
    requestStatus: "Consumed",
    requestReceivedAt: "2026-08-21T00:00:00Z",
  };
}

function items(from: number, count: number) { return Array.from({ length: count }, (_, index) => item(from + index)); }

function page(direction: SessionRunMetadataPage["direction"], rows: SessionRunMetadataItem[], options: { requestCursor?: string | null; head?: number; hasOlder?: boolean } = {}): SessionRunMetadataPage {
  const hasOlder = options.hasOlder ?? false;
  return {
    selector,
    sessionId: selector.sessionId!,
    direction,
    requestCursor: direction === "Tail" ? null : options.requestCursor ?? "cursor",
    limit: SESSION_RUN_METADATA_PAGE_LIMIT,
    membershipHeadRunNumber: options.head ?? 640,
    anchorRootRunId: null,
    consistency: "MembershipHeadOnly",
    items: rows,
    omitted: { older: hasOlder, newer: direction === "Older" },
    continuation: { olderCursor: hasOlder ? `before-${rows[0]?.runNumber ?? 1}` : null, returnToTail: direction === "Older" },
  };
}

beforeEach(() => { vi.useFakeTimers(); getPage.mockReset(); });
afterEach(() => vi.useRealTimers());
async function settle() { await act(async () => { await Promise.resolve(); }); }

describe("useSessionRunMetadataWindow", () => {
  it("does no I/O without a mounted selector and starts one local Tail window when selected", async () => {
    getPage.mockResolvedValue(page("Tail", items(513, 128), { hasOlder: true }));
    const { result, rerender } = renderHook(({ selected }) => useSessionRunMetadataWindow(selected ? selector : undefined, false), { initialProps: { selected: false } });
    await settle();
    expect(getPage).not.toHaveBeenCalled();

    rerender({ selected: true });
    await settle();
    expect(getPage).toHaveBeenCalledExactlyOnceWith(selector, { direction: "Tail", limit: SESSION_RUN_METADATA_PAGE_LIMIT }, expect.any(AbortSignal));
    expect(result.current.items).toHaveLength(128);
    expect(result.current.olderOmitted).toBe(true);
    expect(result.current.consistency).toBe("MembershipHeadOnly");
  });

  it("prepends Older on demand, freezes the head, caps local rows, and explicitly accounts for both omitted sides", async () => {
    getPage.mockResolvedValueOnce(page("Tail", items(513, 128), { hasOlder: true }));
    for (const start of [385, 257, 129, 1]) getPage.mockResolvedValueOnce(page("Older", items(start, 128), { requestCursor: `before-${start + 128}`, hasOlder: start > 1 }));
    const { result } = renderHook(() => useSessionRunMetadataWindow(selector, false));
    await settle();
    for (let index = 0; index < 4; index += 1) await act(async () => result.current.loadOlder());

    expect(result.current.items).toHaveLength(SESSION_RUN_METADATA_WINDOW_LIMIT);
    expect(result.current.items[0].runNumber).toBe(1);
    expect(result.current.items.at(-1)?.runNumber).toBe(512);
    expect(result.current.olderOmitted).toBe(false);
    expect(result.current.newerOmitted).toBe(true);
    expect(result.current.atLatest).toBe(false);
    expect(getPage.mock.calls.slice(1).every((call) => call[1].membershipHeadRunNumber === 640)).toBe(true);
  });

  it("polls by Tail replacement only while active/latest, and Return latest performs a fresh Tail", async () => {
    getPage.mockResolvedValueOnce(page("Tail", items(513, 128), { hasOlder: true })).mockResolvedValueOnce(page("Tail", items(600, 2), { head: 641 }));
    const { result } = renderHook(() => useSessionRunMetadataWindow(selector, true));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(SESSION_RUN_METADATA_POLL_MS));
    expect(result.current.items.map(({ runNumber }) => runNumber)).toEqual([600, 601]);

    getPage.mockResolvedValueOnce(page("Older", items(472, 128), { requestCursor: "before-600", head: 641, hasOlder: true }));
    await act(async () => result.current.loadOlder());
    await act(() => vi.advanceTimersByTimeAsync(SESSION_RUN_METADATA_POLL_MS * 4));
    expect(getPage).toHaveBeenCalledTimes(3);

    getPage.mockResolvedValueOnce(page("Tail", items(700, 2), { head: 701 }));
    act(() => result.current.returnToLatest());
    await settle();
    expect(result.current.items.map(({ runNumber }) => runNumber)).toEqual([700, 701]);
  });

  it("retains the last valid page, stops on closed/nontransient errors, and caps transient 5xx retries", async () => {
    getPage.mockResolvedValueOnce(page("Tail", [item(1)]));
    const { result } = renderHook(() => useSessionRunMetadataWindow(selector, true));
    await settle();
    getPage.mockRejectedValue(new ApiError(500, "http_500", "down"));
    await act(() => vi.advanceTimersByTimeAsync(SESSION_RUN_METADATA_POLL_MS * 32));
    expect(result.current.items.map(({ runNumber }) => runNumber)).toEqual([1]);
    expect(getPage.mock.calls.length).toBeLessThanOrEqual(5);

    getPage.mockRejectedValueOnce(new InvalidSessionRunMetadataPageError());
    act(() => result.current.returnToLatest());
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(SESSION_RUN_METADATA_POLL_MS * 32));
    const afterInvalid = getPage.mock.calls.length;
    await act(() => vi.advanceTimersByTimeAsync(SESSION_RUN_METADATA_POLL_MS * 32));
    expect(getPage).toHaveBeenCalledTimes(afterInvalid);
  });

  it.each([403, 404])("stops polling on HTTP %s", async (status) => {
    getPage.mockRejectedValue(new ApiError(status, `http_${status}`, "closed"));
    renderHook(() => useSessionRunMetadataWindow(selector, true));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(SESSION_RUN_METADATA_POLL_MS * 32));
    expect(getPage).toHaveBeenCalledTimes(1);
  });

  it("aborts in-flight work and fences stale replies on selector switch and unmount", async () => {
    let resolveFirst!: (value: SessionRunMetadataPage) => void;
    let resolveSecond!: (value: SessionRunMetadataPage) => void;
    getPage.mockImplementationOnce((_selector: SessionRunMetadataSelector, _request: unknown, signal: AbortSignal) => new Promise((resolve) => { expect(signal.aborted).toBe(false); resolveFirst = resolve; }))
      .mockImplementationOnce((_selector: SessionRunMetadataSelector, _request: unknown, signal: AbortSignal) => new Promise((resolve) => { expect(signal.aborted).toBe(false); resolveSecond = resolve; }));
    const anchor: SessionRunMetadataSelector = { kind: "RunAnchor", sessionId: null, runAnchorId: anchorRunId };
    const { result, rerender, unmount } = renderHook(({ selected }) => useSessionRunMetadataWindow(selected, false), { initialProps: { selected: selector as SessionRunMetadataSelector | undefined } });
    await settle();
    const firstSignal = getPage.mock.calls[0][2] as AbortSignal;
    rerender({ selected: anchor });
    expect(firstSignal.aborted).toBe(true);
    await settle();
    await act(async () => resolveFirst(page("Tail", [item(1)])));
    expect(result.current.items).toEqual([]);
    const secondSignal = getPage.mock.calls[1][2] as AbortSignal;
    unmount();
    expect(secondSignal.aborted).toBe(true);
    await act(async () => resolveSecond({ ...page("Tail", [item(20)]), selector: anchor, anchorRootRunId: anchorRunId }));
  });
});
