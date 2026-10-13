import { afterEach, describe, expect, it, vi } from "vitest";

import { InvalidWorkflowRunPendingWaitObservationError, workflowsApi } from "./workflows";

const runId = "11111111-1111-1111-1111-111111111111";
const waitId = "22222222-2222-2222-2222-222222222222";

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run pending-wait API", () => {
  it("reads the bounded route and preserves an honest truncated prompt", async () => {
    const prefix = "x".repeat(2048);
    vi.stubGlobal("fetch", vi.fn(() => json({ runId, wait: { id: waitId, nodeId: "approval", kind: "Approval", token: "token", wakeAt: null, promptState: "Truncated", promptPrefix: prefix } })));

    const result = await workflowsApi.getRunPendingWait(runId);

    expect(result?.wait).toEqual({ id: waitId, nodeId: "approval", kind: "Approval", token: "token", wakeAt: null, promptState: "Truncated", promptPrefix: prefix });
    expect(fetch).toHaveBeenCalledTimes(1);
    expect(String(vi.mocked(fetch).mock.calls[0][0]).endsWith(`/api/workflows/runs/${runId}/pending-wait`)).toBe(true);
  });

  it("fails closed on extra fields and inconsistent prompt completeness", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(json({ runId, wait: null, payload: { secret: true } }))
      .mockResolvedValueOnce(json({ runId, wait: { id: waitId, nodeId: "approval", kind: "Approval", token: "token", wakeAt: null, promptState: "Exact", promptPrefix: null } }))
      .mockResolvedValueOnce(json({ runId, wait: { id: waitId, nodeId: "approval", kind: "Approval", token: "token", wakeAt: null, promptState: "Invalid", promptPrefix: "must-not-cross" } })));

    await expect(workflowsApi.getRunPendingWait(runId)).rejects.toBeInstanceOf(InvalidWorkflowRunPendingWaitObservationError);
    await expect(workflowsApi.getRunPendingWait(runId)).rejects.toBeInstanceOf(InvalidWorkflowRunPendingWaitObservationError);
    await expect(workflowsApi.getRunPendingWait(runId)).rejects.toBeInstanceOf(InvalidWorkflowRunPendingWaitObservationError);
  });

  it("reads a Callback wait whose token the server withholds — the placeholder passes the decoder every SPA build ships", async () => {
    // The literal the API sends in place of a Callback's token (WorkflowWaitKinds.WithheldBearerToken, pinned by
    // WorkflowWaitKindsTests). A null here showed "Pending action unavailable" on an SPA built before the reveal existed.
    const withheld = "withheld-reload-the-page-to-reveal";
    vi.stubGlobal("fetch", vi.fn(() => json({ runId, wait: { id: waitId, nodeId: "cb", kind: "Callback", token: withheld, wakeAt: null, promptState: "Missing", promptPrefix: null } })));

    const result = await workflowsApi.getRunPendingWait(runId);

    expect(result?.wait).toEqual({ id: waitId, nodeId: "cb", kind: "Callback", token: withheld, wakeAt: null, promptState: "Missing", promptPrefix: null });
  });

  it("reveals a callback URL through the runs.control-gated command", async () => {
    vi.stubGlobal("fetch", vi.fn(() => json({ waitId, path: "/api/workflows/callbacks/0123456789abcdef0123456789abcdef" })));

    const revealed = await workflowsApi.revealCallbackUrl(runId, waitId);

    expect(revealed).toEqual({ waitId, path: "/api/workflows/callbacks/0123456789abcdef0123456789abcdef" });
    const [url, init] = vi.mocked(fetch).mock.calls[0] as [string, RequestInit];
    expect(String(url).endsWith(`/api/workflows/runs/${runId}/waits/${waitId}/callback-url`)).toBe(true);
    expect(init.method).toBe("POST");
  });

  it("conflates an inaccessible run with missing", async () => {
    vi.stubGlobal("fetch", vi.fn(() => json({ title: "Not Found" }, 404)));
    await expect(workflowsApi.getRunPendingWait(runId)).resolves.toBeNull();
  });
});
