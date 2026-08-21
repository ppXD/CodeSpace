import { afterEach, describe, expect, it, vi } from "vitest";

import {
  InvalidWorkflowRunCellFieldRangeResponseError,
  WORKFLOW_RUN_CELL_FIELD_PAGE_BYTES,
  workflowRunCellFieldRangeApi,
  type WorkflowRunCellFieldReadIdentity,
} from "./workflowRunCellFieldRangeApi";

const requestedRunId = "11111111-1111-4111-8111-111111111111";
const sourceRunId = "22222222-2222-4222-8222-222222222222";
const stateRecordId = "33333333-3333-4333-8333-333333333333";
const firstStartedRecordId = "44444444-4444-4444-8444-444444444444";

const identity: WorkflowRunCellFieldReadIdentity = {
  requestedRunId,
  scope: "LineageMerged",
  sourceRunId,
  nodeId: "worker",
  iterationKey: "branch-0",
  stateRecordId,
  stateRecordSequence: 42,
  firstStartedRecordId,
  firstStartedRecordSequence: 17,
  section: "Output",
  name: "",
};

function available(over: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    ...identity,
    status: "Success",
    availability: "Available",
    source: "Inline",
    requestCursor: null,
    limitBytes: WORKFLOW_RUN_CELL_FIELD_PAGE_BYTES,
    offsetBytes: 0,
    returnedBytes: 7,
    totalBytes: 7,
    nextCursor: null,
    text: "{\"x\":1}",
    contentType: "application/json",
    integrityVerified: true,
    completeJsonValue: true,
    retryable: false,
    ...over,
  };
}

function unavailable(availability: string, retryable = false): Record<string, unknown> {
  const source = availability === "StaleIdentity" || availability === "NotRecorded" ? "Unavailable" : "Artifact";
  return available({
    availability,
    source,
    returnedBytes: 0,
    totalBytes: null,
    nextCursor: null,
    text: null,
    contentType: source === "Artifact" ? "application/json" : null,
    integrityVerified: false,
    completeJsonValue: false,
    retryable,
  });
}

function response(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run cell-field range API", () => {
  it("sends the full exact identity, fixed byte cap and AbortSignal while preserving an empty property name", async () => {
    let request!: URL;
    let signal!: AbortSignal;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      request = new URL(String(input), "http://test.local");
      signal = init.signal as AbortSignal;
      return response(available());
    }));
    const controller = new AbortController();

    const result = await workflowRunCellFieldRangeApi.read(identity, { cursor: null, offsetBytes: 0 }, controller.signal);

    expect(request.pathname).toBe(`/api/workflows/runs/${requestedRunId}/cells/fields/range`);
    expect(Object.fromEntries(request.searchParams)).toEqual({
      scope: "LineageMerged",
      sourceRunId,
      nodeId: "worker",
      iterationKey: "branch-0",
      stateRecordId,
      stateRecordSequence: "42",
      firstStartedRecordId,
      firstStartedRecordSequence: "17",
      section: "Output",
      name: "",
      limitBytes: String(WORKFLOW_RUN_CELL_FIELD_PAGE_BYTES),
    });
    expect(signal).toBe(controller.signal);
    expect(result).toMatchObject({ requestedRunId, sourceRunId, name: "", offsetBytes: 0, completeJsonValue: true });
  });

  it("accepts every non-empty .NET Guid shape rather than imposing RFC version or variant bits", async () => {
    const nonRfcIdentity = {
      ...identity,
      requestedRunId: "77777777-7777-7777-f777-777777777777",
      sourceRunId: "88888888-8888-0888-0888-888888888888",
    };
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue(response(available(nonRfcIdentity))));

    await expect(workflowRunCellFieldRangeApi.read(nonRfcIdentity, { cursor: null, offsetBytes: 0 }))
      .resolves.toMatchObject(nonRfcIdentity);
  });

  it("validates every echoed identity, cursor, offset, limit and range figure", async () => {
    const mutations: Record<string, unknown>[] = [
      { requestedRunId: sourceRunId }, { scope: "FutureScope" }, { sourceRunId: requestedRunId }, { nodeId: "other" },
      { iterationKey: "other" }, { stateRecordId: firstStartedRecordId }, { stateRecordSequence: 43 },
      { firstStartedRecordId: null }, { firstStartedRecordSequence: null }, { section: "Input" }, { name: "other" },
      { requestCursor: "wrong" }, { limitBytes: 1 }, { offsetBytes: 1 }, { returnedBytes: 8 }, { totalBytes: 6 },
      { nextCursor: "unexpected" }, { contentType: "text/plain" }, { text: "x" }, { completeJsonValue: false },
      { artifactId: "55555555-5555-4555-8555-555555555555" },
    ];
    vi.stubGlobal("fetch", vi.fn());
    for (const mutation of mutations) {
      vi.mocked(globalThis.fetch).mockResolvedValueOnce(response(available(mutation)));
      await expect(workflowRunCellFieldRangeApi.read(identity, { cursor: null, offsetBytes: 0 }))
        .rejects.toBeInstanceOf(InvalidWorkflowRunCellFieldRangeResponseError);
    }
  });

  it("accepts only closed availability/source/status values and only BackendUnavailable may be retryable", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(response(unavailable("BackendUnavailable", true)))
      .mockResolvedValueOnce(response(unavailable("IntegrityFailure")))
      .mockResolvedValueOnce(response(unavailable("FutureAvailability")))
      .mockResolvedValueOnce(response(unavailable("IntegrityFailure", true)))
      .mockResolvedValueOnce(response({ ...unavailable("NotRecorded"), source: "Artifact" }))
      .mockResolvedValueOnce(response({ ...unavailable("BackendUnavailable", true), source: "Unavailable" }))
      .mockResolvedValueOnce(response(available({ source: "FutureSource" })))
      .mockResolvedValueOnce(response(available({ status: "FutureStatus" }))));

    await expect(workflowRunCellFieldRangeApi.read(identity, { cursor: null, offsetBytes: 0 })).resolves.toMatchObject({ availability: "BackendUnavailable", retryable: true });
    await expect(workflowRunCellFieldRangeApi.read(identity, { cursor: null, offsetBytes: 0 })).resolves.toMatchObject({ availability: "IntegrityFailure", retryable: false });
    for (let index = 0; index < 6; index += 1)
      await expect(workflowRunCellFieldRangeApi.read(identity, { cursor: null, offsetBytes: 0 })).rejects.toBeInstanceOf(InvalidWorkflowRunCellFieldRangeResponseError);
  });

  it("pins continuation arithmetic, UTF-8 byte counts and complete-only JSON", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(response(available({ requestCursor: "opaque", offsetBytes: 7, text: "💾", returnedBytes: 4, totalBytes: 20, nextCursor: "next", completeJsonValue: false })))
      .mockResolvedValueOnce(response(available({ text: "\ud800", returnedBytes: 3, totalBytes: 3 })))
      .mockResolvedValueOnce(response(available({ text: "not-json", returnedBytes: 8, totalBytes: 8 })))
      .mockResolvedValueOnce(response(available({ nextCursor: "next", completeJsonValue: true, totalBytes: 20 }))));

    await expect(workflowRunCellFieldRangeApi.read(identity, { cursor: "opaque", offsetBytes: 7 })).resolves.toMatchObject({ text: "💾", nextCursor: "next" });
    for (let index = 0; index < 3; index += 1)
      await expect(workflowRunCellFieldRangeApi.read(identity, { cursor: null, offsetBytes: 0 })).rejects.toBeInstanceOf(InvalidWorkflowRunCellFieldRangeResponseError);
  });

  it("conflates 404 and rejects malformed local identity/cursor before I/O", async () => {
    const fetchSpy = vi.fn().mockResolvedValueOnce(response({}, 404));
    vi.stubGlobal("fetch", fetchSpy);

    await expect(workflowRunCellFieldRangeApi.read(identity, { cursor: null, offsetBytes: 0 })).resolves.toBeNull();
    await expect(workflowRunCellFieldRangeApi.read({ ...identity, requestedRunId: "bad" }, { cursor: null, offsetBytes: 0 }))
      .rejects.toBeInstanceOf(InvalidWorkflowRunCellFieldRangeResponseError);
    await expect(workflowRunCellFieldRangeApi.read(identity, { cursor: " ", offsetBytes: 1 }))
      .rejects.toBeInstanceOf(InvalidWorkflowRunCellFieldRangeResponseError);
    await expect(workflowRunCellFieldRangeApi.read(identity, { cursor: null, offsetBytes: Number.MAX_SAFE_INTEGER + 1 }))
      .rejects.toBeInstanceOf(InvalidWorkflowRunCellFieldRangeResponseError);
    expect(fetchSpy).toHaveBeenCalledTimes(1);
  });
});
