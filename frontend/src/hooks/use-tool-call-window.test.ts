import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/api/request";
import { InvalidToolCallPageError, type ToolCallPageRequest, type ToolCallPageResponse, type ToolCallView } from "@/api/agents";

const { getPage } = vi.hoisted(() => ({ getPage: vi.fn() }));
vi.mock("@/api/agents", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/agents")>();
  return { ...original, agentsApi: { ...original.agentsApi, pageToolCalls: getPage } };
});

import { TOOL_CALL_PAGE_LIMIT, TOOL_CALL_WINDOW_LIMIT, TOOL_CALL_WINDOW_POLL_MS, useToolCallWindow } from "./use-agents";

function call(index: number): ToolCallView {
  return { toolKind: `tool.${index}`, status: "Succeeded", createdDate: new Date(Date.UTC(2026, 7, 21, 0, 0, index)).toISOString(), lastModifiedDate: new Date(Date.UTC(2026, 7, 21, 0, 0, index)).toISOString(), error: null, approvedByUserId: null, approvedAt: null };
}

function calls(from: number, count: number): ToolCallView[] {
  return Array.from({ length: count }, (_, index) => call(from + index));
}

function page(mode: ToolCallPageResponse["mode"], rows: ToolCallView[], options: { runId?: string; requestCursor?: string | null; hasOlder?: boolean; nextOlderCursor?: string | null } = {}): ToolCallPageResponse {
  return { agentRunId: options.runId ?? "run-1", mode, requestCursor: mode === "Tail" ? null : options.requestCursor ?? "cursor", items: rows, hasOlder: options.hasOlder ?? false, nextOlderCursor: options.nextOlderCursor ?? (options.hasOlder ? `before-${rows[0]?.toolKind}` : null) };
}

beforeEach(() => { vi.useFakeTimers(); getPage.mockReset(); });
afterEach(() => vi.useRealTimers());

async function settle() {
  await act(async () => { await Promise.resolve(); });
}

describe("useToolCallWindow", () => {
  it("loads a bounded Tail, replaces it while active, and stops polling when terminal", async () => {
    getPage.mockResolvedValueOnce(page("Tail", [call(1)])).mockResolvedValueOnce(page("Tail", [call(2)]));
    const { result, rerender } = renderHook(({ active }) => useToolCallWindow("run-1", active), { initialProps: { active: true } });
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS));

    expect(getPage.mock.calls.map((entry) => entry[1])).toEqual([{ mode: "Tail", limit: TOOL_CALL_PAGE_LIMIT }, { mode: "Tail", limit: TOOL_CALL_PAGE_LIMIT }]);
    expect(result.current.data.map((row) => row.toolKind)).toEqual(["tool.2"]);
    rerender({ active: false });
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS * 10));
    expect(getPage).toHaveBeenCalledTimes(2);
  });

  it("prepends Older manually, hard-caps 512, pauses live refresh, and returns to a fresh Tail", async () => {
    getPage.mockResolvedValueOnce(page("Tail", calls(513, TOOL_CALL_PAGE_LIMIT), { hasOlder: true, nextOlderCursor: "c513" }));
    for (const start of [385, 257, 129, 1]) getPage.mockResolvedValueOnce(page("Older", calls(start, TOOL_CALL_PAGE_LIMIT), { requestCursor: `c${start + TOOL_CALL_PAGE_LIMIT}`, hasOlder: start > 1, nextOlderCursor: start > 1 ? `c${start}` : null }));
    getPage.mockResolvedValueOnce(page("Tail", [call(900)]));

    const { result } = renderHook(() => useToolCallWindow("run-1", true));
    await settle();
    for (let i = 0; i < 4; i += 1) await act(async () => result.current.loadOlder());
    expect(result.current.data).toHaveLength(TOOL_CALL_WINDOW_LIMIT);
    expect(result.current.data[0].toolKind).toBe("tool.1");
    expect(result.current.atLatest).toBe(false);
    expect(result.current.newerItemsOmitted).toBe(true);
    const beforePoll = getPage.mock.calls.length;
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS * 10));
    expect(getPage).toHaveBeenCalledTimes(beforePoll);

    act(() => result.current.returnToLatest());
    await settle();
    expect(result.current.data.map((row) => row.toolKind)).toEqual(["tool.900"]);
    expect(result.current.atLatest).toBe(true);
    expect((getPage.mock.calls.at(-1)?.[1] as ToolCallPageRequest).mode).toBe("Tail");
  });

  it("preserves the last valid Tail across transient 5xx with bounded backoff, but stops on 404, 403, and invalid wire", async () => {
    getPage
      .mockResolvedValueOnce(page("Tail", [call(1)]))
      .mockRejectedValueOnce(new ApiError(500, "temporary", "Temporary"))
      .mockResolvedValueOnce(page("Tail", [call(2)]))
      .mockRejectedValueOnce(new ApiError(404, "missing", "NotFound"));
    const first = renderHook(() => useToolCallWindow("run-1", true));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS));
    expect(first.result.current.data[0].toolKind).toBe("tool.1");
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS * 2));
    expect(first.result.current.data[0].toolKind).toBe("tool.2");
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS));
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS * 10));
    expect(getPage).toHaveBeenCalledTimes(4);
    first.unmount();

    getPage.mockReset().mockResolvedValueOnce(page("Tail", [call(1)])).mockRejectedValueOnce(new InvalidToolCallPageError());
    const second = renderHook(() => useToolCallWindow("run-2", true));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS));
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS * 10));
    expect(getPage).toHaveBeenCalledTimes(2);
    second.unmount();

    getPage.mockReset().mockResolvedValueOnce(page("Tail", [call(1)])).mockRejectedValueOnce(new ApiError(403, "denied", "AccessDenied"));
    const third = renderHook(() => useToolCallWindow("run-3", true));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS));
    await act(() => vi.advanceTimersByTimeAsync(TOOL_CALL_WINDOW_POLL_MS * 10));
    expect(getPage).toHaveBeenCalledTimes(2);
    third.unmount();
  });

  it("aborts Tail and Older work on run switch/unmount behind a generation fence", async () => {
    let resolveTail!: (value: ToolCallPageResponse) => void;
    let resolveOlder!: (value: ToolCallPageResponse) => void;
    getPage
      .mockImplementationOnce(() => new Promise<ToolCallPageResponse>((resolve) => { resolveTail = resolve; }))
      .mockResolvedValueOnce(page("Tail", [call(20)], { runId: "run-2", hasOlder: true, nextOlderCursor: "c20" }))
      .mockImplementationOnce(() => new Promise<ToolCallPageResponse>((resolve) => { resolveOlder = resolve; }));
    const { result, rerender, unmount } = renderHook(({ runId }) => useToolCallWindow(runId, false), { initialProps: { runId: "run-1" as string | undefined } });
    await settle();
    const firstSignal = getPage.mock.calls[0][2] as AbortSignal;
    rerender({ runId: "run-2" });
    expect(firstSignal.aborted).toBe(true);
    await settle();
    await act(async () => resolveTail(page("Tail", [call(1)])));
    expect(result.current.data[0].toolKind).toBe("tool.20");
    act(() => { void result.current.loadOlder(); });
    const olderSignal = getPage.mock.calls[2][2] as AbortSignal;
    unmount();
    expect(olderSignal.aborted).toBe(true);
    await act(async () => resolveOlder(page("Older", [call(1)], { runId: "run-2", requestCursor: "c20" })));
  });
});
