import { afterEach, describe, expect, it, vi } from "vitest";

import { agentsApi, InvalidAgentRunEventPageError } from "./agents";

const runId = "11111111-1111-1111-1111-111111111111";
const artifactId = "AbCdEf00-2222-3333-4444-555555555555";

function event(sequence: number) {
  return { sequence, kind: "ToolCall", text: `event-${sequence}`, data: " {\"raw\":true} ", dataArtifactId: artifactId, occurredAt: "2026-08-21T00:00:00Z" };
}

function page(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    agentRunId: runId,
    mode: "Tail",
    requestCursor: null,
    kindFilter: null,
    items: [event(8), event(9)],
    hasOlder: true,
    hasNewer: false,
    nextOlderCursor: "8",
    nextNewerCursor: "9",
    ...over,
  };
}

function json(body: unknown) {
  return new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("Agent Run event page API", () => {
  it("requests exact run/mode/cursor windows, propagates AbortSignal, and preserves data/artifact identity", async () => {
    const requests: Array<{ url: URL; signal?: AbortSignal }> = [];
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = new URL(String(input), "http://test.local");
      requests.push({ url, signal: init.signal as AbortSignal });
      if (url.searchParams.get("direction") === "Older") return json(page({ mode: "Older", requestCursor: "8", items: [event(7)], nextOlderCursor: null, nextNewerCursor: "7", hasOlder: false, hasNewer: true }));
      if (url.searchParams.get("direction") === "Newer") return json(page({ mode: "Newer", requestCursor: "9", items: [event(10)], nextOlderCursor: "10", nextNewerCursor: "10", hasOlder: true }));
      return json(page());
    }));
    const controller = new AbortController();

    const tail = await agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 }, controller.signal);
    await agentsApi.pageRunEvents(runId, { mode: "Older", cursor: "8", limit: 128 }, controller.signal);
    await agentsApi.pageRunEvents(runId, { mode: "Newer", cursor: "9", limit: 128 }, controller.signal);

    expect(requests.map(({ url }) => `${url.pathname}?${url.searchParams}`)).toEqual([
      `/api/agents/runs/${runId}/events/page?direction=Tail&limit=128`,
      `/api/agents/runs/${runId}/events/page?direction=Older&limit=128&cursor=8`,
      `/api/agents/runs/${runId}/events/page?direction=Newer&limit=128&cursor=9`,
    ]);
    expect(requests.every(({ signal }) => signal === controller.signal)).toBe(true);
    expect(tail).toMatchObject({ agentRunId: runId, mode: "Tail", requestCursor: null });
    expect(tail.items[0].data).toBe(" {\"raw\":true} ");
    expect(tail.items[0].dataArtifactId).toBe(artifactId);
  });

  it("sends and verifies the exact open kind filter", async () => {
    let request!: URL;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      request = new URL(String(input), "http://test.local");
      return json(page({ kindFilter: "ToolCall" }));
    }));

    const result = await agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128, kindFilter: "ToolCall" });

    expect(request.searchParams.get("kindFilter")).toBe("ToolCall");
    expect(result.kindFilter).toBe("ToolCall");
  });

  it("fails closed when the response omits or changes the requested kind identity", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json(page()))
      .mockResolvedValueOnce(json(page({ kindFilter: "Reasoning" })))
      .mockResolvedValueOnce(json(page({ kindFilter: "ToolCall", items: [{ ...event(8), kind: "Reasoning" }] }))));

    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128, kindFilter: "ToolCall" })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128, kindFilter: "ToolCall" })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128, kindFilter: "ToolCall" })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
  });

  it("fails closed on descending/duplicate/unsafe events and malformed event fields", async () => {
    const missingArtifact: Record<string, unknown> = { ...event(8) };
    delete missingArtifact.dataArtifactId;
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json(page({ items: [event(9), event(8)], nextOlderCursor: "9" })))
      .mockResolvedValueOnce(json(page({ items: [event(8), event(8)] })))
      .mockResolvedValueOnce(json(page({ items: [{ ...event(8), sequence: Number.MAX_SAFE_INTEGER + 1 }], nextOlderCursor: String(Number.MAX_SAFE_INTEGER + 1), nextNewerCursor: String(Number.MAX_SAFE_INTEGER + 1) })))
      .mockResolvedValueOnce(json(page({ items: [missingArtifact] })))
      .mockResolvedValueOnce(json(page({ items: [{ ...event(8), data: { parsed: true } }] })))
      .mockResolvedValueOnce(json(page({ items: [{ ...event(8), occurredAt: "not-a-date" }] })))
      .mockResolvedValueOnce(json(page({ items: [{ ...event(8), kind: "FutureUnregisteredKind" }] })))
      .mockResolvedValueOnce(json(page({ hasOlder: false, nextOlderCursor: undefined }))));

    for (let i = 0; i < 8; i += 1)
      await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
  });

  it("fails closed on contradictory mode cursors/status flags and over-limit pages", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json(page({ agentRunId: "22222222-2222-2222-2222-222222222222" })))
      .mockResolvedValueOnce(json(page({ mode: "Older" })))
      .mockResolvedValueOnce(json(page({ requestCursor: "8" })))
      .mockResolvedValueOnce(json(page({ hasNewer: true })))
      .mockResolvedValueOnce(json(page({ nextOlderCursor: "7" })))
      .mockResolvedValueOnce(json(page({ items: [event(8)], nextOlderCursor: "8", nextNewerCursor: "7" })))
      .mockResolvedValueOnce(json(page({ mode: "Older", requestCursor: "8", items: [event(8)], nextOlderCursor: null, nextNewerCursor: "8", hasOlder: false })))
      .mockResolvedValueOnce(json(page({ mode: "Newer", requestCursor: "9", items: [event(9)], nextOlderCursor: "9", nextNewerCursor: "9", hasOlder: true })))
      .mockResolvedValueOnce(json(page({ items: [], nextOlderCursor: null, nextNewerCursor: "0", hasOlder: true })))
      .mockResolvedValueOnce(json(page({ items: Array.from({ length: 129 }, (_, index) => event(index + 1)), nextOlderCursor: "1", nextNewerCursor: "129" }))));

    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Older", cursor: "8", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Newer", cursor: "9", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidAgentRunEventPageError);
  });

  it("rejects invalid request cursors and limits before I/O", async () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);

    await expect(agentsApi.pageRunEvents(runId, { mode: "Older", cursor: "0", limit: 128 })).rejects.toThrow(/invalid Agent Run event page request/i);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Newer", cursor: " 1", limit: 128 })).rejects.toThrow(/invalid Agent Run event page request/i);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 501 })).rejects.toThrow(/invalid Agent Run event page request/i);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128, kindFilter: "" })).rejects.toThrow(/invalid Agent Run event page request/i);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128, kindFilter: " " })).rejects.toThrow(/invalid Agent Run event page request/i);
    await expect(agentsApi.pageRunEvents(runId, { mode: "Tail", limit: 128, kindFilter: "x".repeat(129) })).rejects.toThrow(/invalid Agent Run event page request/i);
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
