import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { read } = vi.hoisted(() => ({ read: vi.fn() }));
vi.mock("@/api/workflowRunViewMetadataApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/workflowRunViewMetadataApi")>();
  return { ...original, workflowRunViewMetadataApi: { read } };
});

import { useWorkflowRunViewMetadata, WORKFLOW_RUN_VIEW_METADATA_POLL_MS } from "./use-workflow-run-view-metadata";

const runId = "11111111-1111-4111-8111-111111111111";
function view(status: "Running" | "Success") {
  return { runId, runNumber: 1, workflowId: null, workflowVersion: null, sourceType: "manual", parentRunId: null,
    status, hasError: false, startedAt: null, completedAt: null, createdDate: "2026-08-21T00:00:00Z", scope: "LineageMerged",
    cellsAvailability: "Available", linksAvailability: "Available", cells: [], topologyAvailability: "Available",
    topology: { nodes: [], edges: [] } } as const;
}
function wrapper(client: QueryClient) {
  return function QueryWrapper({ children }: { children: ReactNode }) { return <QueryClientProvider client={client}>{children}</QueryClientProvider>; };
}
async function settle() {
  await act(async () => { await Promise.resolve(); await vi.advanceTimersByTimeAsync(0); });
}

beforeEach(() => { vi.useFakeTimers(); read.mockReset(); });
afterEach(() => vi.useRealTimers());

describe("useWorkflowRunViewMetadata", () => {
  it("issues no request while a non-Canvas Room tab keeps the query disabled", async () => {
    const { rerender } = renderHook(({ enabled }) => useWorkflowRunViewMetadata(runId, enabled), {
      initialProps: { enabled: false }, wrapper: wrapper(new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })),
    });
    await settle();
    expect(read).not.toHaveBeenCalled();

    read.mockResolvedValue(view("Success"));
    rerender({ enabled: true });
    await settle();
    expect(read).toHaveBeenCalledExactlyOnceWith(runId, "LineageMerged", expect.any(AbortSignal));
  });

  it("polls the bounded metadata while active and stops after its terminal observation", async () => {
    read.mockResolvedValueOnce(view("Running")).mockResolvedValueOnce(view("Success"));
    renderHook(() => useWorkflowRunViewMetadata(runId, true), {
      wrapper: wrapper(new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Infinity } } })),
    });
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(WORKFLOW_RUN_VIEW_METADATA_POLL_MS));
    await settle();
    await act(() => vi.advanceTimersByTimeAsync(WORKFLOW_RUN_VIEW_METADATA_POLL_MS * 3));

    expect(read).toHaveBeenCalledTimes(2);
  });
});
