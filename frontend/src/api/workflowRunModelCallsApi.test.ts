import { afterEach, describe, expect, it, vi } from "vitest";

import { InvalidWorkflowRunModelCallPageError, workflowsApi } from "./workflows";

const runId = "11111111-1111-1111-1111-111111111111";
const callId = "22222222-2222-2222-2222-222222222222";

function call(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    workflowRunModelCallId: callId,
    runId,
    callOrdinal: 7,
    nodeId: "agent",
    iterationKey: "agent#0",
    executionAttemptId: null,
    purpose: "agent.model-call",
    requestedProvider: "OpenAI",
    requestedModel: "gpt-5",
    captureSource: "agent-run-record/v1",
    captureCompleteness: "Exact",
    createdAt: "2026-08-21T01:00:00Z",
    ...over,
  };
}

function page(over: Record<string, unknown> = {}): Record<string, unknown> {
  return { runId, requestCursor: null, limit: 100, items: [call()], nextCursor: null, ...over };
}

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run model-call index API", () => {
  it("requests and validates one metadata-only cross-producer page", async () => {
    let request!: URL;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      request = new URL(String(input), "http://test.local");
      return response(page());
    }));

    const result = await workflowsApi.pageRunModelCalls(runId);

    expect(`${request.pathname}?${request.searchParams}`).toBe(`/api/workflows/runs/${runId}/model-calls?limit=100`);
    expect(result?.items[0]).toMatchObject({ workflowRunModelCallId: callId, purpose: "agent.model-call", captureSource: "agent-run-record/v1" });
    expect(JSON.stringify(result)).not.toMatch(/prompt|response|body|artifact/i);
  });

  it.each([
    ["foreign run", { runId: "44444444-4444-4444-4444-444444444444" }],
    ["wrong limit", { limit: 99 }],
    ["invalid call id", { items: [call({ workflowRunModelCallId: "bad" })] }],
    ["future completeness", { items: [call({ captureCompleteness: "Future" })] }],
    ["invalid timestamp", { items: [call({ createdAt: "soon" })] }],
    ["unexpected cursor", { nextCursor: "more" }],
  ])("fails closed on %s", async (_name, mutation) => {
    vi.stubGlobal("fetch", vi.fn(() => response(page(mutation))));
    await expect(workflowsApi.pageRunModelCalls(runId)).rejects.toBeInstanceOf(InvalidWorkflowRunModelCallPageError);
  });

  it("rejects invalid requests before I/O and conflates an inaccessible run with missing", async () => {
    const fetchSpy = vi.fn().mockResolvedValue(response({}, 404));
    vi.stubGlobal("fetch", fetchSpy);

    await expect(workflowsApi.pageRunModelCalls("bad")).rejects.toBeInstanceOf(InvalidWorkflowRunModelCallPageError);
    await expect(workflowsApi.pageRunModelCalls(runId, " ")).rejects.toBeInstanceOf(InvalidWorkflowRunModelCallPageError);
    await expect(workflowsApi.pageRunModelCalls(runId, undefined, 201)).rejects.toBeInstanceOf(InvalidWorkflowRunModelCallPageError);
    expect(fetchSpy).not.toHaveBeenCalled();

    await expect(workflowsApi.pageRunModelCalls(runId)).resolves.toBeNull();
  });
});
