import { afterEach, describe, expect, it, vi } from "vitest";

import { InvalidWorkflowRunToolCallResponseError, workflowsApi } from "./workflows";

const runId = "11111111-1111-1111-1111-111111111111";
const callId = "22222222-2222-2222-2222-222222222222";
const sourceId = "33333333-3333-3333-3333-333333333333";

function call(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    toolCallId: callId,
    runId,
    toolAdapterKind: "governed-tool-call/v1",
    toolName: "git.open_pr",
    effectClass: "SideEffecting",
    state: "Completed",
    callOrdinal: 7,
    sourceKind: "tool-call-ledger/v1",
    sourceCorrelationId: sourceId,
    captureSource: "tool-call-ledger/v1",
    captureCompleteness: "Unavailable",
    createdAt: "2026-08-21T01:00:00Z",
    lastModifiedAt: "2026-08-21T01:01:00Z",
    terminalAt: "2026-08-21T01:01:00Z",
    errorCode: null,
    ...over,
  };
}

function page(over: Record<string, unknown> = {}): Record<string, unknown> {
  return { runId, requestCursor: null, limit: 128, items: [call()], nextCursor: null, ...over };
}

function attempt(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    attemptOrdinal: 1,
    status: "Succeeded",
    captureSource: "tool-call-ledger/v1",
    captureCompleteness: "Unavailable",
    startedAt: "2026-08-21T01:00:00Z",
    completedAt: "2026-08-21T01:01:00Z",
    createdAt: "2026-08-21T01:00:00Z",
    lastModifiedAt: "2026-08-21T01:01:00Z",
    errorCode: null,
    ...over,
  };
}

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run governed tool-call API", () => {
  it("requests exact run/cursor/limit pages, echoes their identity, and propagates AbortSignal", async () => {
    let request!: URL;
    let signal!: AbortSignal;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      request = new URL(String(input), "http://test.local");
      signal = init.signal as AbortSignal;
      return response(page({ requestCursor: "opaque-v1", nextCursor: null }));
    }));
    const controller = new AbortController();

    const result = await workflowsApi.pageRunToolCalls(runId, { cursor: "opaque-v1", limit: 128 }, controller.signal);

    expect(`${request.pathname}?${request.searchParams}`).toBe(`/api/workflows/runs/${runId}/tool-calls?limit=128&cursor=opaque-v1`);
    expect(signal).toBe(controller.signal);
    expect(result).toMatchObject({ runId, requestCursor: "opaque-v1", limit: 128, nextCursor: null });
    expect(result?.items[0]).toMatchObject({ toolCallId: callId, state: "Completed", captureCompleteness: "Unavailable" });
  });

  it("accepts Corrupt/LegacyUnknown as explicit evidence but rejects truly unknown wire enums", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(response(page({ items: [call({ effectClass: "Corrupt", state: "LegacyUnknown", captureCompleteness: "Corrupt", errorCode: "LegacyUnknown" })] })))
      .mockResolvedValueOnce(response(page({ items: [call({ state: "FutureState" })] })))
      .mockResolvedValueOnce(response(page({ items: [call({ captureCompleteness: "FutureCapture" })] }))));

    await expect(workflowsApi.pageRunToolCalls(runId, { limit: 128 })).resolves.toMatchObject({ items: [{ state: "LegacyUnknown", effectClass: "Corrupt" }] });
    await expect(workflowsApi.pageRunToolCalls(runId, { limit: 128 })).rejects.toBeInstanceOf(InvalidWorkflowRunToolCallResponseError);
    await expect(workflowsApi.pageRunToolCalls(runId, { limit: 128 })).rejects.toBeInstanceOf(InvalidWorkflowRunToolCallResponseError);
  });

  it.each([
    ["foreign run", { runId: "44444444-4444-4444-4444-444444444444" }],
    ["wrong request cursor", { requestCursor: "different" }],
    ["wrong limit", { limit: 127 }],
    ["unstable id", { items: [call({ toolCallId: "not-a-guid" })] }],
    ["invalid timestamp", { items: [call({ terminalAt: "not-a-date" })] }],
    ["unsafe ordinal", { items: [call({ callOrdinal: Number.MAX_SAFE_INTEGER + 1 })] }],
    ["non-descending keyset order", { items: [call({ createdAt: "2026-08-21T01:00:00Z" }), call({ toolCallId: sourceId, createdAt: "2026-08-21T02:00:00Z" })] }],
    ["over-limit page", { items: Array.from({ length: 129 }, () => call({ toolCallId: crypto.randomUUID() })) }],
  ])("fails closed on %s", async (_name, mutation) => {
    vi.stubGlobal("fetch", vi.fn(() => response(page(mutation))));
    await expect(workflowsApi.pageRunToolCalls(runId, { limit: 128 })).rejects.toBeInstanceOf(InvalidWorkflowRunToolCallResponseError);
  });

  it("reads one exact stable-id detail with ordered capped metadata-only attempts", async () => {
    let requested = "";
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      requested = String(input);
      return response({ call: call(), attempts: [attempt(), attempt({ attemptOrdinal: 2, status: "Indeterminate", errorCode: "LedgerFailedOutcomeUnknown" })], attemptsTruncated: false });
    }));

    const result = await workflowsApi.getRunToolCall(runId, callId);

    expect(requested.endsWith(`/api/workflows/runs/${runId}/tool-calls/${callId}`)).toBe(true);
    expect(result?.call.toolCallId).toBe(callId);
    expect(result?.attempts.map(({ attemptOrdinal }) => attemptOrdinal)).toEqual([1, 2]);
    expect(result?.attempts[1]).toMatchObject({ status: "Indeterminate", errorCode: "LedgerFailedOutcomeUnknown" });
    expect(JSON.stringify(result)).not.toMatch(/artifact|argument|resultJson|errorMessage|endpoint|invocation|approval|idempotency/i);
  });

  it("conflates detail/list 404 and fails closed on detail identity, ordering, cap, or future status", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(response({}, 404))
      .mockResolvedValueOnce(response({}, 404))
      .mockResolvedValueOnce(response({ call: call({ toolCallId: sourceId }), attempts: [], attemptsTruncated: false }))
      .mockResolvedValueOnce(response({ call: call(), attempts: [attempt({ attemptOrdinal: 2 }), attempt()], attemptsTruncated: false }))
      .mockResolvedValueOnce(response({ call: call(), attempts: Array.from({ length: 101 }, (_, index) => attempt({ attemptOrdinal: index + 1 })), attemptsTruncated: true }))
      .mockResolvedValueOnce(response({ call: call(), attempts: [attempt({ status: "FutureAttempt" })], attemptsTruncated: false })));

    await expect(workflowsApi.pageRunToolCalls(runId, { limit: 128 })).resolves.toBeNull();
    await expect(workflowsApi.getRunToolCall(runId, callId)).resolves.toBeNull();
    for (let index = 0; index < 4; index += 1)
      await expect(workflowsApi.getRunToolCall(runId, callId)).rejects.toBeInstanceOf(InvalidWorkflowRunToolCallResponseError);
  });

  it("rejects invalid request ids, cursor and limits before I/O", async () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);

    await expect(workflowsApi.pageRunToolCalls("bad", { limit: 128 })).rejects.toBeInstanceOf(InvalidWorkflowRunToolCallResponseError);
    await expect(workflowsApi.pageRunToolCalls(runId, { cursor: " ", limit: 128 })).rejects.toBeInstanceOf(InvalidWorkflowRunToolCallResponseError);
    await expect(workflowsApi.pageRunToolCalls(runId, { cursor: "x".repeat(97), limit: 128 })).rejects.toBeInstanceOf(InvalidWorkflowRunToolCallResponseError);
    await expect(workflowsApi.pageRunToolCalls(runId, { limit: 201 })).rejects.toBeInstanceOf(InvalidWorkflowRunToolCallResponseError);
    await expect(workflowsApi.getRunToolCall(runId, "bad")).rejects.toBeInstanceOf(InvalidWorkflowRunToolCallResponseError);
    expect(fetchSpy).not.toHaveBeenCalled();
  });
});
