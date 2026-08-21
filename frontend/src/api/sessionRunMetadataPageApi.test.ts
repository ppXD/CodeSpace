import { afterEach, describe, expect, it, vi } from "vitest";

import { InvalidSessionRunMetadataPageError, sessionsApi, type SessionRunMetadataPageRequest, type SessionRunMetadataSelector } from "./sessions";

const sessionId = "11111111-1111-1111-1111-111111111111";
const runId = "22222222-2222-2222-2222-222222222222";
const requestId = "33333333-3333-3333-3333-333333333333";
const sessionSelector: SessionRunMetadataSelector = { kind: "Session", sessionId, runAnchorId: null };
const anchorSelector: SessionRunMetadataSelector = { kind: "RunAnchor", sessionId: null, runAnchorId: runId };

function item(runNumber = 8) {
  return {
    runId,
    runNumber,
    runRequestId: requestId,
    rootRunId: null,
    sessionTurnIndex: 1,
    status: "Running",
    projectionKind: { text: null, sizeBytes: 0, state: "None" },
    sourceType: { text: "snapshot", sizeBytes: 8, state: "Complete" },
    rerunFromNodeId: { text: null, sizeBytes: 0, state: "None" },
    createdDate: "2026-08-21T00:00:00Z",
    startedAt: null,
    completedAt: null,
    error: { text: "x".repeat(512), sizeBytes: 513, state: "Truncated" },
    requestStatus: "Consumed",
    requestReceivedAt: "2026-08-20T23:59:00Z",
  };
}

function page(over: Record<string, unknown> = {}) {
  return {
    selector: sessionSelector,
    sessionId,
    direction: "Tail",
    requestCursor: null,
    limit: 128,
    membershipHeadRunNumber: 9,
    anchorRootRunId: null,
    consistency: "MembershipHeadOnly",
    items: [item(8), { ...item(9), runId: "44444444-4444-4444-4444-444444444444" }],
    omitted: { older: true, newer: false },
    continuation: { olderCursor: "opaque", returnToTail: false },
    ...over,
  };
}

function json(body: unknown) { return new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } }); }

afterEach(() => vi.unstubAllGlobals());

describe("Session run metadata page API", () => {
  it("uses exact Session/RunAnchor routes and verifies every echoed request coordinate", async () => {
    const requests: Array<{ url: URL; signal?: AbortSignal }> = [];
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = new URL(String(input), "http://test.local");
      requests.push({ url, signal: init.signal as AbortSignal });
      if (url.pathname.includes("by-run")) return json(page({ selector: anchorSelector, anchorRootRunId: runId }));
      return json(page());
    }));
    const controller = new AbortController();

    await sessionsApi.pageRunMetadata(sessionSelector, { direction: "Tail", limit: 128 }, controller.signal);
    await sessionsApi.pageRunMetadata(anchorSelector, { direction: "Tail", limit: 128 }, controller.signal);

    expect(requests.map(({ url }) => `${url.pathname}?${url.searchParams}`)).toEqual([
      `/api/sessions/${sessionId}/runs/page?direction=Tail&limit=128`,
      `/api/sessions/by-run/${runId}/runs/page?direction=Tail&limit=128`,
    ]);
    expect(requests.every(({ signal }) => signal === controller.signal)).toBe(true);
  });

  it("accepts an exact Older continuation bound to the frozen membership head", async () => {
    const request: SessionRunMetadataPageRequest = { direction: "Older", cursor: "opaque", membershipHeadRunNumber: 9, limit: 128 };
    vi.stubGlobal("fetch", vi.fn(() => json(page({ direction: "Older", requestCursor: "opaque", items: [item(7)], omitted: { older: false, newer: true }, continuation: { olderCursor: null, returnToTail: true } }))));

    const result = await sessionsApi.pageRunMetadata(sessionSelector, request);

    expect(result.membershipHeadRunNumber).toBe(9);
    expect(result.items.map(({ runNumber }) => runNumber)).toEqual([7]);
  });

  it("fails closed on selector/head/direction/cursor/limit/continuation contradictions", async () => {
    const invalid = [
      page({ selector: anchorSelector }),
      page({ sessionId: runId }),
      page({ direction: "Older" }),
      page({ requestCursor: "unexpected" }),
      page({ limit: 127 }),
      page({ membershipHeadRunNumber: Number.MAX_SAFE_INTEGER + 1 }),
      page({ consistency: "Snapshot" }),
      page({ omitted: { older: false, newer: false }, continuation: { olderCursor: "opaque", returnToTail: false } }),
      page({ omitted: { older: true, newer: true } }),
      page({ continuation: { olderCursor: null, returnToTail: true } }),
    ];
    vi.stubGlobal("fetch", vi.fn().mockImplementation(() => json(invalid.shift())));

    for (let index = 0; index < 10; index += 1)
      await expect(sessionsApi.pageRunMetadata(sessionSelector, { direction: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidSessionRunMetadataPageError);
  });

  it("fails closed on unknown enums, unsafe membership, duplicates, malformed timestamps and bounded-text lies", async () => {
    const invalidItems = [
      { ...item(), status: "Future" },
      { ...item(), requestStatus: "Future" },
      { ...item(), runNumber: 10 },
      { ...item(), runNumber: Number.MAX_SAFE_INTEGER + 1 },
      { ...item(), createdDate: "invalid" },
      { ...item(), projectionKind: { text: "x", sizeBytes: 1, state: "None" } },
      { ...item(), sourceType: { text: "snapshot", sizeBytes: 99, state: "Complete" } },
      { ...item(), error: { text: "x", sizeBytes: 1, state: "Corrupt" } },
      { ...item(), error: { text: "x".repeat(513), sizeBytes: 514, state: "Truncated" } },
    ];
    const invalidPages = invalidItems.map((row) => page({ items: [row], continuation: { olderCursor: "opaque", returnToTail: false } }));
    invalidPages.push(page({ items: [item(8), item(8)] }));
    invalidPages.push(page({ items: [item(9), item(8)] }));
    vi.stubGlobal("fetch", vi.fn().mockImplementation(() => json(invalidPages.shift())));

    for (let index = 0; index < 11; index += 1)
      await expect(sessionsApi.pageRunMetadata(sessionSelector, { direction: "Tail", limit: 128 })).rejects.toBeInstanceOf(InvalidSessionRunMetadataPageError);
  });

  it("rejects malformed selectors and page ranges before network I/O", async () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);
    const invalidSelector = { kind: "Session", sessionId, runAnchorId: runId } as unknown as SessionRunMetadataSelector;

    await expect(sessionsApi.pageRunMetadata(invalidSelector, { direction: "Tail", limit: 128 })).rejects.toThrow(/invalid Session run metadata page request/i);
    await expect(sessionsApi.pageRunMetadata(sessionSelector, { direction: "Tail", cursor: "x", limit: 128 })).rejects.toThrow(/invalid Session run metadata page request/i);
    await expect(sessionsApi.pageRunMetadata(sessionSelector, { direction: "Older", limit: 128 })).rejects.toThrow(/invalid Session run metadata page request/i);
    await expect(sessionsApi.pageRunMetadata(sessionSelector, { direction: "Older", cursor: "x", membershipHeadRunNumber: 0, limit: 128 })).rejects.toThrow(/invalid Session run metadata page request/i);
    await expect(sessionsApi.pageRunMetadata(sessionSelector, { direction: "Tail", limit: 257 })).rejects.toThrow(/invalid Session run metadata page request/i);
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
