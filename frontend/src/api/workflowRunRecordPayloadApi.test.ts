import { afterEach, describe, expect, it, vi } from "vitest";

import { workflowsApi } from "./workflows";

const runId = "11111111-1111-4111-8111-111111111111";
const recordId = "22222222-2222-4222-8222-222222222222";

function available(bytes: Uint8Array, over: Record<string, string> = {}) {
  const headers = new Headers({
    "X-CodeSpace-Workflow-Run-Id": runId,
    "X-CodeSpace-Workflow-Run-Record-Id": recordId,
    "X-CodeSpace-Workflow-Run-Record-Sequence": "42",
    "X-CodeSpace-Workflow-Run-Record-Payload-Offset": "0",
    "X-CodeSpace-Workflow-Run-Record-Payload-Total-Bytes": String(bytes.byteLength),
    "X-CodeSpace-Workflow-Run-Record-Payload-Content-Type": "application/json",
    "Content-Type": "application/octet-stream",
    ...over,
  });
  return new Response(Uint8Array.from(bytes).buffer, { status: 200, headers });
}

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run record payload API", () => {
  it("reads an exact bounded range, propagates AbortSignal, and validates identity headers", async () => {
    const bytes = new TextEncoder().encode('{"raw":true}');
    const controller = new AbortController();
    const fetchSpy = vi.fn().mockResolvedValue(available(bytes));
    vi.stubGlobal("fetch", fetchSpy);

    const result = await workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 64 * 1024, controller.signal);

    expect(fetchSpy).toHaveBeenCalledTimes(1);
    const [requested, init] = fetchSpy.mock.calls[0];
    expect(String(requested).endsWith(`/api/workflows/runs/${runId}/records/${recordId}/payload?offsetBytes=0&limitBytes=65536`)).toBe(true);
    expect(init).toEqual(expect.objectContaining({ signal: controller.signal }));
    expect(result).toMatchObject({ availability: "Available", runId, recordId, sequence: 42, offsetBytes: 0, totalBytes: bytes.byteLength, nextOffsetBytes: null, contentType: "application/json" });
    if (result.availability === "Available") expect(Array.from(result.bytes)).toEqual(Array.from(bytes));
  });

  it("fails closed on contradictory identity/range/content headers", async () => {
    const bytes = new TextEncoder().encode("{}");
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(available(bytes, { "X-CodeSpace-Workflow-Run-Id": "foreign" }))
      .mockResolvedValueOnce(available(bytes, { "X-CodeSpace-Workflow-Run-Record-Sequence": "43" }))
      .mockResolvedValueOnce(available(bytes, { "X-CodeSpace-Workflow-Run-Record-Payload-Total-Bytes": "1" }))
      .mockResolvedValueOnce(available(bytes, { "X-CodeSpace-Workflow-Run-Record-Payload-Content-Type": "text/plain" }))
      .mockResolvedValueOnce(available(bytes, { "Content-Type": "application/json" })));

    for (let i = 0; i < 5; i += 1)
      await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 64 * 1024)).resolves.toEqual({ availability: "InvalidResponse", code: "invalid_record_payload_range_headers", isRetryable: false });
  });

  it("classifies missing, exact invalid-range problems, and transient transport without inventing availability", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(new Response(null, { status: 404 }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ runId, recordId, sequence: 42, availability: "InvalidRange", code: "InvalidRange", isRetryable: false }), { status: 400, headers: { "Content-Type": "application/json" } }))
      .mockRejectedValueOnce(new TypeError("network")));

    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 1)).resolves.toMatchObject({ availability: "Missing", isRetryable: false });
    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 2_147_483_647, 1)).resolves.toEqual({ availability: "InvalidRange", code: "InvalidRange", isRetryable: false });
    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 1)).resolves.toEqual({ availability: "BackendUnavailable", code: "transport_unavailable", isRetryable: true });
  });

  it("rejects invalid local ranges before I/O", async () => {
    const fetchSpy = vi.fn();
    vi.stubGlobal("fetch", fetchSpy);

    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, -1, 1)).resolves.toMatchObject({ availability: "InvalidResponse", isRetryable: false });
    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 64 * 1024 + 1)).resolves.toMatchObject({ availability: "InvalidResponse", isRetryable: false });
    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, Number.MAX_SAFE_INTEGER, 1)).resolves.toMatchObject({ availability: "InvalidResponse", isRetryable: false });
    expect(fetchSpy).not.toHaveBeenCalled();
  });

  it("stops consuming a contradictory response body beyond the requested byte cap", async () => {
    const bytes = new Uint8Array(65).fill(1);
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(available(bytes)));

    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 64)).resolves.toEqual({ availability: "InvalidResponse", code: "invalid_record_payload_body_length", isRetryable: false });
  });

  it("requires Content-Length to match consumed bytes and classifies 408/429 as manually retryable", async () => {
    const bytes = new TextEncoder().encode("{}");
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(available(bytes, { "Content-Length": "1" }))
      .mockResolvedValueOnce(new Response(null, { status: 408 }))
      .mockResolvedValueOnce(new Response(null, { status: 429 }))
      .mockResolvedValueOnce(new Response(null, { status: 403 })));

    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 64)).resolves.toEqual({ availability: "InvalidResponse", code: "invalid_record_payload_body_length", isRetryable: false });
    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 64)).resolves.toEqual({ availability: "BackendUnavailable", code: "http_408", isRetryable: true });
    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 64)).resolves.toEqual({ availability: "BackendUnavailable", code: "http_429", isRetryable: true });
    await expect(workflowsApi.readRunRecordPayloadRange(runId, recordId, 42, 0, 64)).resolves.toEqual({ availability: "AccessDenied", code: "http_403", isRetryable: false });
  });
});
