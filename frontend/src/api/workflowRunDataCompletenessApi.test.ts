import { afterEach, describe, expect, it, vi } from "vitest";

import { workflowsApi } from "./workflows";

const runId = "11111111-1111-1111-1111-111111111111";

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function valid() {
  return {
    runId,
    scope: "RecordedFacetsOnly",
    facets: [
      {
        facet: "native-record",
        expectedRecordCount: 2,
        presentRecordCount: 1,
        knownMissingCount: 1,
        verdict: "Partial",
        isStrictlyReadable: false,
        revision: 3,
        schemaVersion: 1,
        lastModifiedAt: "2026-08-21T02:00:00Z",
      },
    ],
    hasStatements: true,
    runWideVerdict: null,
    truncated: false,
  };
}

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run data completeness API", () => {
  it("reads the exact run-scoped metadata view without requesting record or artifact content", async () => {
    let requested = "";
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL) => {
      requested = String(input);
      return response(valid());
    }));

    const result = await workflowsApi.getRunDataCompleteness(runId);

    expect(requested).toBe(`/api/workflows/runs/${runId}/data-completeness`);
    expect(result).toEqual(valid());
  });

  it("404-conflates a missing or foreign run", async () => {
    vi.stubGlobal("fetch", vi.fn(() => response({}, 404)));

    await expect(workflowsApi.getRunDataCompleteness(runId)).resolves.toBeNull();
  });

  it.each([
    ["foreign identity", { runId: "foreign" }],
    ["future scope", { scope: "AllKnownFacets" }],
    ["invented run verdict", { runWideVerdict: "Exact" }],
    ["unsafe count", { facets: [{ ...valid().facets[0], presentRecordCount: Number.MAX_SAFE_INTEGER + 1 }] }],
    ["readability contradiction", { facets: [{ ...valid().facets[0], verdict: "Exact", isStrictlyReadable: false }] }],
    ["statement contradiction", { facets: [], hasStatements: true }],
  ])("fails closed on %s", async (_name, mutation) => {
    vi.stubGlobal("fetch", vi.fn(() => response({ ...valid(), ...mutation })));

    await expect(workflowsApi.getRunDataCompleteness(runId)).rejects.toThrow(/invalid workflow run data completeness/i);
  });
});
