import { afterEach, describe, expect, it, vi } from "vitest";

import { workflowsApi, type RunRecordPageResponse } from "./workflows";

const runId = "11111111-1111-1111-1111-111111111111";

function page(over: Partial<RunRecordPageResponse> = {}): RunRecordPageResponse {
  return {
    runId,
    runStatus: "Running",
    mode: "Tail",
    records: [
      { sequence: 8, recordType: "log", nodeId: null, iterationKey: "", occurredAt: "2026-08-21T00:00:00Z", payloadJson: " {\"raw\":true} ", correlationId: null, parentRecordId: null },
      { sequence: 9, recordType: "run.started", nodeId: "node-1", iterationKey: "i", occurredAt: "2026-08-21T00:00:01Z", payloadJson: "{}", correlationId: "c", parentRecordId: "p" },
    ],
    nextBeforeSequence: 8,
    nextAfterSequence: null,
    ...over,
  };
}

function json(body: unknown) {
  return new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run record page API", () => {
  it("requests an exact bounded mode, propagates AbortSignal, and preserves raw payloadJson bytes", async () => {
    const requests: Array<{ url: URL; signal?: AbortSignal }> = [];
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      const url = new URL(String(input), "http://test.local");
      requests.push({ url, signal: init.signal as AbortSignal });
      if (url.searchParams.has("beforeSequence")) return json(page({ mode: "Older", records: [page().records[0]], nextBeforeSequence: null }));
      if (url.searchParams.has("afterSequence")) return json(page({ mode: "Newer", records: [{ ...page().records[1], sequence: 10 }], nextBeforeSequence: null, nextAfterSequence: 10 }));
      return json(page());
    }));
    const controller = new AbortController();

    const tail = await workflowsApi.getRunRecordPage(runId, { limit: 128 }, controller.signal);
    await workflowsApi.getRunRecordPage(runId, { beforeSequence: 9, limit: 128 }, controller.signal);
    await workflowsApi.getRunRecordPage(runId, { afterSequence: 9, limit: 128 }, controller.signal);

    expect(requests.map(({ url }) => `${url.pathname}?${url.searchParams}`)).toEqual([
      `/api/workflows/runs/${runId}/records/page?limit=128`,
      `/api/workflows/runs/${runId}/records/page?limit=128&beforeSequence=9`,
      `/api/workflows/runs/${runId}/records/page?limit=128&afterSequence=9`,
    ]);
    expect(requests.every(({ signal }) => signal === controller.signal)).toBe(true);
    expect(tail.records[0].payloadJson).toBe(" {\"raw\":true} ");
  });

  it("fails closed on foreign identity, unknown/wrong mode, contradictory continuation, and cursor violations", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json(page({ runId: "foreign" })))
      .mockResolvedValueOnce(json(page({ mode: "Future" as "Tail" })))
      .mockResolvedValueOnce(json(page({ mode: "Older" })))
      .mockResolvedValueOnce(json(page({ nextAfterSequence: 9 })))
      .mockResolvedValueOnce(json(page({ mode: "Older", records: [{ ...page().records[0], sequence: 10 }], nextBeforeSequence: null })))
      .mockResolvedValueOnce(json(page({ mode: "Newer", records: [{ ...page().records[0], sequence: 9 }], nextBeforeSequence: null, nextAfterSequence: null }))));

    await expect(workflowsApi.getRunRecordPage(runId, { limit: 128 })).rejects.toThrow(/invalid workflow run record page/i);
    await expect(workflowsApi.getRunRecordPage(runId, { limit: 128 })).rejects.toThrow(/invalid workflow run record page/i);
    await expect(workflowsApi.getRunRecordPage(runId, { limit: 128 })).rejects.toThrow(/invalid workflow run record page/i);
    await expect(workflowsApi.getRunRecordPage(runId, { limit: 128 })).rejects.toThrow(/invalid workflow run record page/i);
    await expect(workflowsApi.getRunRecordPage(runId, { beforeSequence: 9, limit: 128 })).rejects.toThrow(/invalid workflow run record page/i);
    await expect(workflowsApi.getRunRecordPage(runId, { afterSequence: 9, limit: 128 })).rejects.toThrow(/invalid workflow run record page/i);
  });

  it("fails closed before rendering descending/duplicate/unsafe sequences or malformed record fields", async () => {
    const missingNodeId: Record<string, unknown> = { ...page().records[0] };
    delete missingNodeId.nodeId;
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json(page({ records: [{ ...page().records[0], sequence: 9 }, { ...page().records[1], sequence: 8 }], nextBeforeSequence: null })))
      .mockResolvedValueOnce(json(page({ records: [{ ...page().records[0], sequence: 8 }, { ...page().records[1], sequence: 8 }], nextBeforeSequence: null })))
      .mockResolvedValueOnce(json(page({ records: [{ ...page().records[0], sequence: Number.MAX_SAFE_INTEGER + 1 }], nextBeforeSequence: null })))
      .mockResolvedValueOnce(json(page({ records: [{ ...page().records[0], payloadJson: 42 as unknown as string }], nextBeforeSequence: null })))
      .mockResolvedValueOnce(json(page({ records: [missingNodeId as unknown as RunRecordPageResponse["records"][number]], nextBeforeSequence: null }))));

    for (let i = 0; i < 5; i += 1)
      await expect(workflowsApi.getRunRecordPage(runId, { limit: 128 })).rejects.toThrow(/invalid workflow run record page/i);
  });

  it("rejects invalid request cursors and limits without issuing I/O", async () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);

    await expect(workflowsApi.getRunRecordPage(runId, { beforeSequence: 9, afterSequence: 8, limit: 128 })).rejects.toThrow(/invalid workflow run record page request/i);
    await expect(workflowsApi.getRunRecordPage(runId, { beforeSequence: 0, limit: 128 })).rejects.toThrow(/invalid workflow run record page request/i);
    await expect(workflowsApi.getRunRecordPage(runId, { afterSequence: -1, limit: 128 })).rejects.toThrow(/invalid workflow run record page request/i);
    await expect(workflowsApi.getRunRecordPage(runId, { limit: 501 })).rejects.toThrow(/invalid workflow run record page request/i);
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
