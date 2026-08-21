import { afterEach, describe, expect, it, vi } from "vitest";

import { adaptWorkflowRunViewToCanvas, InvalidWorkflowRunViewMetadataError, workflowRunLazyFieldRead, workflowRunViewMetadataApi } from "./workflowRunViewMetadataApi";

const runId = "11111111-1111-4111-8111-111111111111";
const sourceRunId = "22222222-2222-4222-8222-222222222222";

function metadata(over: Record<string, unknown> = {}) {
  return {
    runId, runNumber: 7, workflowId: null, workflowVersion: 3, sourceType: "manual", parentRunId: null,
    status: "Running", hasError: false, startedAt: "2026-08-21T00:00:01Z", completedAt: null,
    createdDate: "2026-08-21T00:00:00Z", scope: "LineageMerged", cellsAvailability: "Available", linksAvailability: "Available",
    cells: [{ sourceRunId, nodeId: "worker", iterationKey: "", containerKind: null, status: "Running",
      startedAt: "2026-08-21T00:00:01Z", completedAt: null, childRunId: null, agentRunId: null, rerunnableFromHere: true }],
    topologyAvailability: "Available",
    topology: { nodes: [{ id: "worker", typeKey: "agent.run", label: "Worker", parentId: null,
      position: { x: 10, y: 20 }, width: null, height: null }], edges: [] },
    ...over,
  };
}

function response(value: unknown, status = 200) { return new Response(JSON.stringify(value), { status, headers: { "Content-Type": "application/json" } }); }

afterEach(() => vi.unstubAllGlobals());

describe("Workflow Run view metadata API", () => {
  it("reads one exact bounded scope and adapts only topology/cell metadata into the Room canvas", async () => {
    let request!: URL;
    const controller = new AbortController();
    vi.stubGlobal("fetch", vi.fn((input: RequestInfo | URL, init: RequestInit = {}) => {
      request = new URL(String(input), "http://test.local");
      expect(init.signal).toBe(controller.signal);
      return response(metadata());
    }));

    const value = await workflowRunViewMetadataApi.read(runId, "LineageMerged", controller.signal);
    const canvas = adaptWorkflowRunViewToCanvas(value!);

    expect(request.pathname).toBe(`/api/workflows/runs/${runId}/view-metadata`);
    expect(Object.fromEntries(request.searchParams)).toEqual({ scope: "LineageMerged" });
    expect(canvas?.definition.nodes[0]).toMatchObject({ id: "worker", config: {}, inputs: {} });
    expect(canvas?.definition.nodes[0]).not.toHaveProperty("prompt");
    expect(canvas?.rows[0]).toMatchObject({ nodeId: "worker", inputs: null, outputs: null, error: null, rerunnableFromHere: true });
    expect(workflowRunLazyFieldRead(canvas!.rows[0])).toMatchObject({ requestedRunId: runId, sourceRunId, nodeId: "worker", iterationKey: "" });
  });

  it("fails closed on unknown enums, identity drift, forbidden body fields and topology contradictions", async () => {
    const mutations = [
      { runId: sourceRunId }, { status: "Future" }, { cellsAvailability: "Future" },
      { normalizedPayload: { secret: true } },
      { topologyAvailability: "Corrupt" },
      { topology: { nodes: [{ ...metadata().topology.nodes[0], config: { secret: true } }], edges: [] } },
      { topology: { nodes: metadata().topology.nodes, edges: [{ from: "worker", to: "missing", sourceHandle: null, targetHandle: null, condition: null }] } },
      { topology: { nodes: [{ ...metadata().topology.nodes[0], parentId: "worker" }], edges: [] } },
      { topology: { nodes: [{ ...metadata().topology.nodes[0], id: "other" }], edges: [] } },
      { cellsAvailability: "Truncated" },
    ];
    vi.stubGlobal("fetch", vi.fn());
    for (const mutation of mutations) {
      vi.mocked(globalThis.fetch).mockResolvedValueOnce(response(metadata(mutation)));
      await expect(workflowRunViewMetadataApi.read(runId, "LineageMerged")).rejects.toBeInstanceOf(InvalidWorkflowRunViewMetadataError);
    }
  });

  it("preserves typed truncated/corrupt states instead of presenting them as exact empty metadata", async () => {
    vi.stubGlobal("fetch", vi.fn()
      .mockResolvedValueOnce(response(metadata({ cellsAvailability: "Truncated", cells: metadata().cells.map((cell) => ({ ...cell, rerunnableFromHere: false })) })))
      .mockResolvedValueOnce(response(metadata({ cellsAvailability: "Corrupt", linksAvailability: "Unavailable", cells: [] })))
      .mockResolvedValueOnce(response({}, 404)));

    await expect(workflowRunViewMetadataApi.read(runId, "LineageMerged")).resolves.toMatchObject({ cellsAvailability: "Truncated", cells: [{ nodeId: "worker" }] });
    await expect(workflowRunViewMetadataApi.read(runId, "LineageMerged")).resolves.toMatchObject({ cellsAvailability: "Corrupt", cells: [] });
    await expect(workflowRunViewMetadataApi.read(runId, "LineageMerged")).resolves.toBeNull();
  });
});
