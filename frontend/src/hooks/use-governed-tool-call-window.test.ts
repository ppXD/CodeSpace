import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/api/request";
import { InvalidWorkflowRunToolCallResponseError, type WorkflowRunToolCallMetadata, type WorkflowRunToolCallPage } from "@/api/workflows";

const { getPage } = vi.hoisted(() => ({ getPage: vi.fn() }));
vi.mock("@/api/workflows", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/workflows")>();
  return { ...original, workflowsApi: { ...original.workflowsApi, pageRunToolCalls: getPage } };
});

import { GOVERNED_TOOL_CALL_PAGE_LIMIT, GOVERNED_TOOL_CALL_POLL_MS, GOVERNED_TOOL_CALL_WINDOW_LIMIT, InvalidWorkflowRunToolCallWindowError, useGovernedToolCallWindow } from "./use-governed-tool-call-window";

function tool(value: number): WorkflowRunToolCallMetadata {
  const id = value.toString(16).padStart(12, "0");
  const at = new Date(Date.UTC(2026, 7, 21, 12, 0, 0) - value * 1000).toISOString();
  return {
    toolCallId: `00000000-0000-0000-0000-${id}`,
    runId: "run-1",
    toolAdapterKind: "governed-tool-call/v1",
    toolName: `tool.${value}`,
    effectClass: "SideEffecting",
    state: "Completed",
    callOrdinal: value,
    sourceKind: "tool-call-ledger/v1",
    sourceCorrelationId: null,
    captureSource: "tool-call-ledger/v1",
    captureCompleteness: "Unavailable",
    createdAt: at,
    lastModifiedAt: at,
    terminalAt: at,
    errorCode: null,
  };
}

function tools(from: number, count: number) {
  return Array.from({ length: count }, (_, index) => tool(from + index));
}

function page(rows: WorkflowRunToolCallMetadata[], requestCursor: string | null = null, hasOlder = false, runId = "run-1"): WorkflowRunToolCallPage {
  return { runId, requestCursor, limit: GOVERNED_TOOL_CALL_PAGE_LIMIT, items: rows, nextCursor: hasOlder ? `before-${rows.at(-1)?.callOrdinal ?? 0}` : null };
}

beforeEach(() => {
  vi.useFakeTimers();
  getPage.mockReset();
});

afterEach(() => vi.useRealTimers());

async function settle() {
  await act(async () => { await Promise.resolve(); });
}

describe("useGovernedToolCallWindow", () => {
  it("loads exactly one first page into React-local state", async () => {
    getPage.mockResolvedValue(page(tools(1, 2)));

    const { result } = renderHook(() => useGovernedToolCallWindow("run-1", false));
    await settle();

    expect(getPage).toHaveBeenCalledExactlyOnceWith("run-1", { limit: GOVERNED_TOOL_CALL_PAGE_LIMIT }, expect.any(AbortSignal));
    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([1, 2]);
    expect(result.current.atLatest).toBe(true);
  });

  it("loads older pages only on demand, caps at 512, marks omitted rows, and returns to a fresh latest page", async () => {
    getPage.mockResolvedValueOnce(page(tools(1, 128), null, true));
    for (const from of [129, 257, 385, 513]) getPage.mockResolvedValueOnce(page(tools(from, 128), `before-${from - 1}`, from < 513));
    getPage.mockResolvedValueOnce(page(tools(900, 2)));

    const { result } = renderHook(() => useGovernedToolCallWindow("run-1", false));
    await settle();
    for (let index = 0; index < 4; index += 1) await act(async () => result.current.loadOlder());

    expect(result.current.calls).toHaveLength(GOVERNED_TOOL_CALL_WINDOW_LIMIT);
    expect(result.current.calls[0].callOrdinal).toBe(129);
    expect(result.current.calls.at(-1)?.callOrdinal).toBe(640);
    expect(result.current.newerCallsOmitted).toBe(true);
    expect(result.current.atLatest).toBe(false);
    expect(getPage.mock.calls.slice(1, 5).map((entry) => entry[1])).toEqual([
      { cursor: "before-128", limit: GOVERNED_TOOL_CALL_PAGE_LIMIT },
      { cursor: "before-256", limit: GOVERNED_TOOL_CALL_PAGE_LIMIT },
      { cursor: "before-384", limit: GOVERNED_TOOL_CALL_PAGE_LIMIT },
      { cursor: "before-512", limit: GOVERNED_TOOL_CALL_PAGE_LIMIT },
    ]);

    act(() => result.current.returnToLatest());
    await settle();
    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([900, 901]);
    expect(result.current.atLatest).toBe(true);
    expect(result.current.newerCallsOmitted).toBe(false);
  });

  it("polls by replacing only the latest first page while active and stops when terminal or browsing older", async () => {
    getPage
      .mockResolvedValueOnce(page(tools(1, 2), null, true))
      .mockResolvedValueOnce(page(tools(10, 2), null, true))
      .mockResolvedValueOnce(page(tools(12, 1), "before-11"));

    const { result, rerender } = renderHook(({ active }) => useGovernedToolCallWindow("run-1", active), { initialProps: { active: true } });
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(GOVERNED_TOOL_CALL_POLL_MS));
    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([10, 11]);

    await act(async () => result.current.loadOlder());
    expect(result.current.atLatest).toBe(false);
    await act(() => vi.advanceTimersByTimeAsync(GOVERNED_TOOL_CALL_POLL_MS * 10));
    expect(getPage).toHaveBeenCalledTimes(3);

    rerender({ active: false });
    getPage.mockResolvedValueOnce(page(tools(20, 1)));
    act(() => result.current.returnToLatest());
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(GOVERNED_TOOL_CALL_POLL_MS * 10));
    expect(getPage).toHaveBeenCalledTimes(4);
  });

  it("retains the last valid page and retries 5xx with bounded backoff", async () => {
    getPage
      .mockResolvedValueOnce(page(tools(1, 2)))
      .mockRejectedValueOnce(new ApiError(503, "unavailable", "unavailable"))
      .mockResolvedValueOnce(page(tools(3, 1)));

    const { result } = renderHook(() => useGovernedToolCallWindow("run-1", true));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(GOVERNED_TOOL_CALL_POLL_MS));
    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([1, 2]);
    expect(result.current.error).toBeInstanceOf(ApiError);
    await act(() => vi.advanceTimersByTimeAsync(GOVERNED_TOOL_CALL_POLL_MS * 2));
    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([3]);
    expect(result.current.error).toBeNull();
  });

  it.each([
    new ApiError(403, "forbidden", "forbidden"),
    new ApiError(404, "missing", "missing"),
    new InvalidWorkflowRunToolCallResponseError(),
  ])("retains the last valid page and permanently stops on %s", async (failure) => {
    getPage.mockResolvedValueOnce(page(tools(1, 2))).mockRejectedValueOnce(failure);

    const { result } = renderHook(() => useGovernedToolCallWindow("run-1", true));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(GOVERNED_TOOL_CALL_POLL_MS));
    await act(() => vi.advanceTimersByTimeAsync(GOVERNED_TOOL_CALL_POLL_MS * 20));

    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([1, 2]);
    expect(result.current.error).toBe(failure);
    expect(getPage).toHaveBeenCalledTimes(2);
  });

  it("fails closed on an overlapping older page without corrupting the valid window", async () => {
    getPage.mockResolvedValueOnce(page(tools(1, 2), null, true)).mockResolvedValueOnce(page([tool(2), tool(3)], "before-2"));

    const { result } = renderHook(() => useGovernedToolCallWindow("run-1", false));
    await settle();
    await act(async () => result.current.loadOlder());

    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([1, 2]);
    expect(result.current.error).toBeInstanceOf(InvalidWorkflowRunToolCallWindowError);
  });

  it("fails closed when an individually valid older page contradicts the cross-page DESC boundary", async () => {
    const outOfOrder = { ...tool(3), createdAt: "2026-08-21T13:00:00Z" };
    getPage.mockResolvedValueOnce(page(tools(1, 2), null, true)).mockResolvedValueOnce(page([outOfOrder], "before-2"));

    const { result } = renderHook(() => useGovernedToolCallWindow("run-1", false));
    await settle();
    await act(async () => result.current.loadOlder());

    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([1, 2]);
    expect(result.current.error).toBeInstanceOf(InvalidWorkflowRunToolCallWindowError);
  });

  it("aborts close/run switch and generation-fences stale list replies", async () => {
    let resolveFirst!: (value: WorkflowRunToolCallPage) => void;
    getPage
      .mockImplementationOnce((_runId: string, _request: unknown, signal: AbortSignal) => new Promise<WorkflowRunToolCallPage>((resolve) => {
        expect(signal.aborted).toBe(false);
        resolveFirst = resolve;
      }))
      .mockResolvedValueOnce(page(tools(20, 1), null, false, "run-2"));

    const { result, rerender, unmount } = renderHook(({ runId, open }) => useGovernedToolCallWindow(open ? runId : undefined, false), { initialProps: { runId: "run-1", open: true } });
    await settle();
    const firstSignal = getPage.mock.calls[0][2] as AbortSignal;

    rerender({ runId: "run-2", open: true });
    expect(firstSignal.aborted).toBe(true);
    await settle();
    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([20]);
    await act(async () => resolveFirst(page(tools(1, 1))));
    expect(result.current.calls.map(({ callOrdinal }) => callOrdinal)).toEqual([20]);

    const secondSignal = getPage.mock.calls[1][2] as AbortSignal;
    rerender({ runId: "run-2", open: false });
    expect(secondSignal.aborted).toBe(true);
    expect(result.current.calls).toEqual([]);
    unmount();
  });
});
