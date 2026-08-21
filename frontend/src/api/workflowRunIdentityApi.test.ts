import { afterEach, describe, expect, it, vi } from "vitest";

import { InvalidWorkflowRunIdentityError, workflowsApi } from "./workflows";

const runId = "11111111-1111-1111-1111-111111111111";

function json(body: unknown) {
  return new Response(JSON.stringify(body), { headers: { "Content-Type": "application/json" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run identity API", () => {
  it("reads the dedicated encoded-ref endpoint and propagates AbortSignal", async () => {
    let request!: URL;
    let requestSignal!: AbortSignal;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      request = new URL(String(input), "http://test.local");
      requestSignal = init.signal as AbortSignal;
      return json({ id: runId, runNumber: 42, status: "Running" });
    }));
    const controller = new AbortController();

    const identity = await workflowsApi.getRunIdentity("legacy ref/1", controller.signal);

    expect(request.pathname).toBe("/api/workflows/runs/legacy%20ref%2F1/identity");
    expect(requestSignal).toBe(controller.signal);
    expect(identity).toEqual({ id: runId, runNumber: 42, status: "Running" });
  });

  it("fails closed on malformed identity and future status wire values", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json({ id: "not-a-guid", runNumber: 42, status: "Running" }))
      .mockResolvedValueOnce(json({ id: runId, runNumber: 0, status: "Running" }))
      .mockResolvedValueOnce(json({ id: runId, runNumber: 42, status: "FutureStatus" }))
      .mockResolvedValueOnce(json({ id: runId, runNumber: 42 })));

    for (let i = 0; i < 4; i += 1) await expect(workflowsApi.getRunIdentity("42")).rejects.toBeInstanceOf(InvalidWorkflowRunIdentityError);
  });
});
