import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { InvalidAgentRunEventPageError, type AgentRunEventDto, type AgentRunEventPageRequest, type AgentRunEventPageResponse } from "@/api/agents";

const { getPage } = vi.hoisted(() => ({ getPage: vi.fn() }));
vi.mock("@/api/agents", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/agents")>();
  return { ...original, agentsApi: { ...original.agentsApi, pageRunEvents: getPage } };
});

import { AGENT_EVENT_PAGE_LIMIT, AGENT_EVENT_WINDOW_LIMIT, AGENT_EVENT_WINDOW_POLL_MS, useAgentRunEventWindow } from "./use-agents";

function event(sequence: number): AgentRunEventDto {
  return { sequence, kind: "Progress", text: `event ${sequence}`, data: null, dataArtifactId: null, occurredAt: "2026-08-21T00:00:00Z" };
}

function events(from: number, count: number): AgentRunEventDto[] {
  return Array.from({ length: count }, (_, index) => event(from + index));
}

function page(mode: AgentRunEventPageResponse["mode"], rows: AgentRunEventDto[], options: { requestCursor?: string | null; hasOlder?: boolean; hasNewer?: boolean; runId?: string } = {}): AgentRunEventPageResponse {
  const requestCursor = mode === "Tail" ? null : options.requestCursor ?? "0";
  return {
    agentRunId: options.runId ?? "run-1",
    mode,
    requestCursor,
    items: rows,
    hasOlder: options.hasOlder ?? false,
    hasNewer: options.hasNewer ?? false,
    nextOlderCursor: options.hasOlder ? String(rows[0]?.sequence ?? requestCursor ?? 0) : null,
    nextNewerCursor: String(rows.at(-1)?.sequence ?? requestCursor ?? 0),
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

describe("useAgentRunEventWindow", () => {
  it("starts with one bounded Tail request and keeps the event page in React-local state", async () => {
    getPage.mockResolvedValue(page("Tail", events(1, 2)));

    const { result } = renderHook(() => useAgentRunEventWindow("run-1", false));
    await settle();

    expect(getPage).toHaveBeenCalledExactlyOnceWith("run-1", { mode: "Tail", limit: AGENT_EVENT_PAGE_LIMIT }, expect.any(AbortSignal));
    expect(result.current.data.map(({ sequence }) => sequence)).toEqual([1, 2]);
    expect(result.current.atLatest).toBe(true);
  });

  it("loads Older only on demand, caps the window, and explicitly marks discarded newer events", async () => {
    getPage.mockResolvedValueOnce(page("Tail", events(513, AGENT_EVENT_PAGE_LIMIT), { hasOlder: true }));
    for (const start of [385, 257, 129, 1])
      getPage.mockResolvedValueOnce(page("Older", events(start, AGENT_EVENT_PAGE_LIMIT), { requestCursor: String(start + AGENT_EVENT_PAGE_LIMIT), hasOlder: start > 1 }));

    const { result } = renderHook(() => useAgentRunEventWindow("run-1", false));
    await settle();
    for (let i = 0; i < 4; i += 1) await act(async () => result.current.loadOlder());

    expect(result.current.data).toHaveLength(AGENT_EVENT_WINDOW_LIMIT);
    expect(result.current.data[0].sequence).toBe(1);
    expect(result.current.data.at(-1)?.sequence).toBe(AGENT_EVENT_WINDOW_LIMIT);
    expect(result.current.atLatest).toBe(false);
    expect(result.current.newerEventsOmitted).toBe(true);
    expect(getPage.mock.calls.slice(1).map((call) => (call[1] as AgentRunEventPageRequest).cursor)).toEqual(["513", "385", "257", "129"]);
  });

  it("polls one Newer request at a time while active, caps old rows, and stops immediately when terminal", async () => {
    let resolveNewer!: (value: AgentRunEventPageResponse) => void;
    getPage
      .mockResolvedValueOnce(page("Tail", events(1, AGENT_EVENT_WINDOW_LIMIT)))
      .mockImplementationOnce(() => new Promise<AgentRunEventPageResponse>((resolve) => { resolveNewer = resolve; }));

    const { result, rerender } = renderHook(({ active }) => useAgentRunEventWindow("run-1", active), { initialProps: { active: true } });
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS));
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS * 4));
    expect(getPage).toHaveBeenCalledTimes(2);

    await act(async () => resolveNewer(page("Newer", [event(513)], { requestCursor: "512" })));
    expect(result.current.data).toHaveLength(AGENT_EVENT_WINDOW_LIMIT);
    expect(result.current.data[0].sequence).toBe(2);
    expect(result.current.olderEventsOmitted).toBe(true);

    rerender({ active: false });
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS * 8));
    expect(getPage).toHaveBeenCalledTimes(2);

    getPage.mockResolvedValueOnce(page("Older", [event(1)], { requestCursor: "2" }));
    await act(async () => result.current.loadOlder());
    expect(getPage.mock.calls[2][1]).toEqual({ mode: "Older", cursor: "2", limit: AGENT_EVENT_PAGE_LIMIT });
    expect(result.current.data[0].sequence).toBe(1);
    expect(result.current.data.at(-1)?.sequence).toBe(512);
  });

  it("continues from the server cursor after an empty healthy Newer page", async () => {
    getPage
      .mockResolvedValueOnce(page("Tail", [event(1)]))
      .mockResolvedValueOnce(page("Newer", [], { requestCursor: "1", hasOlder: true }))
      .mockResolvedValueOnce(page("Newer", [event(2)], { requestCursor: "1", hasOlder: true }));

    const { result } = renderHook(() => useAgentRunEventWindow("run-1", true));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS));
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS));

    expect(getPage.mock.calls.slice(1).map((call) => (call[1] as AgentRunEventPageRequest).cursor)).toEqual(["1", "1"]);
    expect(result.current.data.map(({ sequence }) => sequence)).toEqual([1, 2]);
    expect(result.current.olderEventsOmitted).toBe(false); // Newer.hasOlder describes the query cursor, not a local-window gap.
    expect(result.current.hasOlder).toBe(false);
  });

  it("keeps the last valid window and fail-closes an invalid page without retrying", async () => {
    getPage.mockResolvedValueOnce(page("Tail", events(1, 2))).mockRejectedValueOnce(new InvalidAgentRunEventPageError());

    const { result } = renderHook(() => useAgentRunEventWindow("run-1", true));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS));
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS * 16));

    expect(result.current.data.map(({ sequence }) => sequence)).toEqual([1, 2]);
    expect(result.current.error).toBeInstanceOf(InvalidAgentRunEventPageError);
    expect(getPage).toHaveBeenCalledTimes(2);
  });

  it("retries transient Tail and Newer failures with bounded backoff and clears the error on recovery", async () => {
    getPage
      .mockRejectedValueOnce(new Error("tail unavailable"))
      .mockResolvedValueOnce(page("Tail", [event(1)]))
      .mockRejectedValueOnce(new Error("poll unavailable"))
      .mockResolvedValueOnce(page("Newer", [event(2)], { requestCursor: "1" }));

    const { result } = renderHook(() => useAgentRunEventWindow("run-1", true));
    await settle();
    expect(result.current.error).toEqual(new Error("tail unavailable"));
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS * 2));
    expect(result.current.data.map(({ sequence }) => sequence)).toEqual([1]);

    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS));
    expect(result.current.error).toEqual(new Error("poll unavailable"));
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_WINDOW_POLL_MS * 2));
    expect(result.current.data.map(({ sequence }) => sequence)).toEqual([1, 2]);
    expect(result.current.error).toBeNull();
  });

  it("aborts close/run-switch work and ignores stale replies behind a generation fence", async () => {
    let resolveFirst!: (value: AgentRunEventPageResponse) => void;
    getPage
      .mockImplementationOnce((_runId: string, _request: AgentRunEventPageRequest, signal: AbortSignal) => new Promise<AgentRunEventPageResponse>((resolve) => {
        expect(signal.aborted).toBe(false);
        resolveFirst = resolve;
      }))
      .mockResolvedValueOnce(page("Tail", [event(20)], { runId: "run-2" }));

    const { result, rerender, unmount } = renderHook(({ runId, open }) => useAgentRunEventWindow(open ? runId : undefined, false), { initialProps: { runId: "run-1", open: true } });
    await settle();
    const firstSignal = getPage.mock.calls[0][2] as AbortSignal;

    rerender({ runId: "run-2", open: true });
    expect(firstSignal.aborted).toBe(true);
    await settle();
    expect(result.current.data.map(({ sequence }) => sequence)).toEqual([20]);
    await act(async () => resolveFirst(page("Tail", [event(1)])));
    expect(result.current.data.map(({ sequence }) => sequence)).toEqual([20]);

    const secondSignal = getPage.mock.calls[1][2] as AbortSignal;
    rerender({ runId: "run-2", open: false });
    expect(secondSignal.aborted).toBe(true);
    expect(result.current.data).toEqual([]);
    unmount();
  });

  it("aborts a manually loaded Older page when the terminal unmounts", async () => {
    let resolveOlder!: (value: AgentRunEventPageResponse) => void;
    getPage
      .mockResolvedValueOnce(page("Tail", events(513, AGENT_EVENT_PAGE_LIMIT), { hasOlder: true }))
      .mockImplementationOnce(() => new Promise<AgentRunEventPageResponse>((resolve) => { resolveOlder = resolve; }));

    const { result, unmount } = renderHook(() => useAgentRunEventWindow("run-1", false));
    await settle();
    act(() => { void result.current.loadOlder(); });
    const olderSignal = getPage.mock.calls[1][2] as AbortSignal;
    expect(olderSignal.aborted).toBe(false);

    unmount();
    expect(olderSignal.aborted).toBe(true);
    await act(async () => resolveOlder(page("Older", events(385, AGENT_EVENT_PAGE_LIMIT), { requestCursor: "513", hasOlder: true })));
  });

  it("returns a historical window to a fresh Tail instead of splicing incomparable pages", async () => {
    getPage.mockResolvedValueOnce(page("Tail", events(513, AGENT_EVENT_PAGE_LIMIT), { hasOlder: true }));
    for (const start of [385, 257, 129, 1])
      getPage.mockResolvedValueOnce(page("Older", events(start, AGENT_EVENT_PAGE_LIMIT), { requestCursor: String(start + AGENT_EVENT_PAGE_LIMIT), hasOlder: start > 1 }));
    getPage.mockResolvedValueOnce(page("Tail", events(900, 2)));

    const { result } = renderHook(() => useAgentRunEventWindow("run-1", false));
    await settle();
    for (let i = 0; i < 4; i += 1) await act(async () => result.current.loadOlder());
    expect(result.current.atLatest).toBe(false);

    act(() => result.current.returnToLatest());
    await settle();
    expect(result.current.data.map(({ sequence }) => sequence)).toEqual([900, 901]);
    expect(getPage.mock.calls.at(-1)?.[1]).toEqual({ mode: "Tail", limit: AGENT_EVENT_PAGE_LIMIT });
    expect(result.current.atLatest).toBe(true);
  });
});
