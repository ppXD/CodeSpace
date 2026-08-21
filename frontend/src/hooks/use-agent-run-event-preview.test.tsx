import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { InvalidAgentRunEventPageError, type AgentRunEventDto, type AgentRunEventPageResponse } from "@/api/agents";

const { getPage } = vi.hoisted(() => ({ getPage: vi.fn() }));
vi.mock("@/api/agents", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/agents")>();
  return { ...original, agentsApi: { ...original.agentsApi, pageRunEvents: getPage } };
});

import { AGENT_EVENT_PREVIEW_LIMIT, AGENT_EVENT_PREVIEW_POLL_MS, useAgentRunEventPreview } from "./use-agents";

function event(sequence: number): AgentRunEventDto {
  return { sequence, kind: "Progress", text: `event ${sequence}`, data: null, dataArtifactId: null, occurredAt: "2026-08-21T00:00:00Z" };
}

function page(runId: string, rows: AgentRunEventDto[]): AgentRunEventPageResponse {
  return {
    agentRunId: runId,
    mode: "Tail",
    requestCursor: null,
    items: rows,
    hasOlder: rows[0]?.sequence !== 1,
    hasNewer: false,
    nextOlderCursor: rows[0]?.sequence !== 1 ? String(rows[0]?.sequence) : null,
    nextNewerCursor: String(rows.at(-1)?.sequence ?? 0),
  };
}

function events(from: number): AgentRunEventDto[] {
  return Array.from({ length: AGENT_EVENT_PREVIEW_LIMIT }, (_, index) => event(from + index));
}

function createClient() {
  return new QueryClient({ defaultOptions: { queries: { gcTime: Infinity } } });
}

function wrapper(client: QueryClient) {
  return function QueryWrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

async function settle() {
  await act(async () => {
    await Promise.resolve();
    await vi.advanceTimersByTimeAsync(0);
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  getPage.mockReset();
});

afterEach(() => vi.useRealTimers());

describe("useAgentRunEventPreview", () => {
  it("shares one Tail poll across observers and replaces its bounded cache instead of accumulating history", async () => {
    const client = createClient();
    getPage.mockResolvedValueOnce(page("run-1", events(1))).mockResolvedValueOnce(page("run-1", events(101)));

    const { result } = renderHook(() => ({ tile: useAgentRunEventPreview("run-1", true), footer: useAgentRunEventPreview("run-1", true) }), { wrapper: wrapper(client) });
    await settle();

    expect(getPage).toHaveBeenCalledExactlyOnceWith("run-1", { mode: "Tail", limit: AGENT_EVENT_PREVIEW_LIMIT }, expect.any(AbortSignal));
    expect(result.current.tile.data?.map(({ sequence }) => sequence)).toEqual(result.current.footer.data?.map(({ sequence }) => sequence));
    expect(result.current.tile.data).toHaveLength(AGENT_EVENT_PREVIEW_LIMIT);

    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_PREVIEW_POLL_MS));
    await act(() => vi.advanceTimersByTimeAsync(1));
    expect(getPage).toHaveBeenCalledTimes(2);
    expect(result.current.tile.data?.map(({ sequence }) => sequence)).toEqual(events(101).map(({ sequence }) => sequence));
    expect(client.getQueryData<AgentRunEventPageResponse>(["agent-run-event-preview", "run-1"])?.items).toHaveLength(AGENT_EVENT_PREVIEW_LIMIT);
  });

  it("fail-closes an invalid page without retrying and preserves the last valid preview", async () => {
    getPage.mockResolvedValueOnce(page("run-1", [event(1)])).mockRejectedValueOnce(new InvalidAgentRunEventPageError());

    const { result, rerender } = renderHook(({ active }) => useAgentRunEventPreview("run-1", active), { initialProps: { active: true }, wrapper: wrapper(createClient()) });
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_PREVIEW_POLL_MS));
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_PREVIEW_POLL_MS * 8));

    expect(result.current.data?.map(({ sequence }) => sequence)).toEqual([1]);
    expect(result.current.error).toBeInstanceOf(InvalidAgentRunEventPageError);
    expect(getPage).toHaveBeenCalledTimes(2);

    rerender({ active: false });
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_PREVIEW_POLL_MS * 8));
    expect(getPage).toHaveBeenCalledTimes(2);
  });

  it("stops a healthy preview poll as soon as its run becomes terminal", async () => {
    getPage.mockResolvedValue(page("run-1", [event(1)]));
    const { rerender } = renderHook(({ active }) => useAgentRunEventPreview("run-1", active), { initialProps: { active: true }, wrapper: wrapper(createClient()) });
    await settle();

    rerender({ active: false });
    await act(() => vi.advanceTimersByTimeAsync(AGENT_EVENT_PREVIEW_POLL_MS * 4));

    expect(getPage).toHaveBeenCalledTimes(1);
  });

  it("aborts an in-flight Tail read on run switch and unmount", async () => {
    getPage.mockImplementation(() => new Promise<AgentRunEventPageResponse>(() => {}));
    const { rerender, unmount } = renderHook(({ runId }) => useAgentRunEventPreview(runId, false), { initialProps: { runId: "run-1" }, wrapper: wrapper(createClient()) });
    await settle();
    const firstSignal = getPage.mock.calls[0][2] as AbortSignal;

    rerender({ runId: "run-2" });
    await settle();
    expect(firstSignal.aborted).toBe(true);
    const secondSignal = getPage.mock.calls[1][2] as AbortSignal;

    unmount();
    expect(secondSignal.aborted).toBe(true);
  });

  it("keeps the accumulating legacy event reader exclusive to the complete native ToolCall consumer", () => {
    const sources = import.meta.glob("../components/**/*.tsx", { eager: true, import: "default", query: "?raw" }) as Record<string, string>;
    const users = Object.entries(sources).filter(([path, source]) => !path.includes(".test.") && /\buseAgentRunEvents\b/.test(source)).map(([path]) => path).sort();

    expect(users).toEqual(["../components/workflows/AgentToolCalls.tsx"]);
  });
});
