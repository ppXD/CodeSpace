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
    expect(fetch).toHaveBeenCalledWith(`/api/workflows/runs/${runId}/pending-wait`, expect.anything());
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

  it("conflates an inaccessible run with missing", async () => {
    vi.stubGlobal("fetch", vi.fn(() => json({ title: "Not Found" }, 404)));
    await expect(workflowsApi.getRunPendingWait(runId)).resolves.toBeNull();
  });
});
