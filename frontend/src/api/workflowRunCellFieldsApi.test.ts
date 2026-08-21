import { afterEach, describe, expect, it, vi } from "vitest";

import { InvalidWorkflowRunCellFieldPageError, WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LIMIT, workflowRunCellFieldsApi } from "./workflowRunCellFieldsApi";

const coordinate = {
  requestedRunId: "11111111-1111-4111-8111-111111111111" as const,
  scope: "LineageMerged" as const,
  sourceRunId: "22222222-2222-4222-8222-222222222222",
  nodeId: "worker",
  iterationKey: "branch#0",
};
const stateRecordId = "33333333-3333-4333-8333-333333333333";
const firstStartedRecordId = "44444444-4444-4444-8444-444444444444";

function page(over: Record<string, unknown> = {}) {
  return {
    ...coordinate, stateRecordId, stateRecordSequence: 42, firstStartedRecordId, firstStartedRecordSequence: 17,
    status: "Success", requestCursor: null, limit: WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LIMIT,
    fieldsAvailability: "Available", inputsAvailability: "Available", outputsAvailability: "Available", errorAvailability: "NotRecorded",
    fields: [{ section: "Output", name: "result", jsonKind: "Object", materialization: "Inline", availability: "Available",
      totalBytes: null, sha256: null, contentType: "application/json", problemCode: null }], nextCursor: null,
    ...over,
  };
}
function response(value: unknown, status = 200) { return new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } }); }

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run cell-field descriptor API", () => {
  it("sends the exact cell coordinate, fixed bounded page and opaque cursor without body/storage identity", async () => {
    let request!: URL;
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => { request = new URL(String(input), "http://test.local"); return response(page()); }));

    const value = await workflowRunCellFieldsApi.read(coordinate, null);

    expect(request.pathname).toBe(`/api/workflows/runs/${coordinate.requestedRunId}/cells/fields`);
    expect(Object.fromEntries(request.searchParams)).toEqual({ scope: "LineageMerged", sourceRunId: coordinate.sourceRunId,
      nodeId: "worker", iterationKey: "branch#0", limit: String(WORKFLOW_RUN_CELL_FIELD_DESCRIPTOR_LIMIT) });
    expect(value?.fields[0]).toEqual(expect.objectContaining({ section: "Output", name: "result", materialization: "Inline" }));
    expect(JSON.stringify(value)).not.toMatch(/artifactId|storageUrl|payloadJson/i);
  });

  it("validates every echo, record identity, closed state and descriptor invariant", async () => {
    const mutations = [
      { requestedRunId: coordinate.sourceRunId }, { sourceRunId: coordinate.requestedRunId }, { nodeId: "other" },
      { iterationKey: "other" }, { stateRecordId: "bad" }, { stateRecordSequence: 0 },
      { firstStartedRecordSequence: null }, { status: "Future" }, { requestCursor: "wrong" }, { limit: 500 },
      { fieldsAvailability: "Future" }, { nextCursor: "unexpected" }, { artifactId: stateRecordId },
      { fields: [{ ...page().fields[0], materialization: "Artifact", totalBytes: null, sha256: null }] },
      { fields: [{ ...page().fields[0], section: "Error", name: "result" }] },
      { fields: [page().fields[0], page().fields[0]] },
      { fields: [{ ...page().fields[0], section: "Input", name: "z" }, { ...page().fields[0], section: "Input", name: "a" }] },
      { outputsAvailability: "NotRecorded" },
      { fields: [{ ...page().fields[0], materialization: "Artifact", availability: "Unavailable", problemCode: "MalformedReference" }] },
    ];
    vi.stubGlobal("fetch", vi.fn());
    for (const mutation of mutations) {
      vi.mocked(globalThis.fetch).mockResolvedValueOnce(response(page(mutation)));
      await expect(workflowRunCellFieldsApi.read(coordinate, null)).rejects.toBeInstanceOf(InvalidWorkflowRunCellFieldPageError);
    }
  });

  it("accepts a typed truncated prefix and conflates foreign/missing with 404", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(response(page({ fieldsAvailability: "Truncated", nextCursor: "opaque" })))
      .mockResolvedValueOnce(response({}, 404)));

    await expect(workflowRunCellFieldsApi.read(coordinate, null)).resolves.toMatchObject({ fieldsAvailability: "Truncated", nextCursor: "opaque" });
    await expect(workflowRunCellFieldsApi.read(coordinate, null)).resolves.toBeNull();
  });
});
