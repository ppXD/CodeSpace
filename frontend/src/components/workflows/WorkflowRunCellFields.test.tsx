import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { WorkflowRunCellFieldDescriptor, WorkflowRunCellFieldPage } from "@/api/workflowRunCellFieldsApi";
import { ApiError } from "@/api/request";
import type { WorkflowRunLazyFieldRead } from "@/api/workflowRunViewMetadataApi";

const { descriptorRead, rangeRead } = vi.hoisted(() => ({ descriptorRead: vi.fn(), rangeRead: vi.fn() }));
vi.mock("@/api/workflowRunCellFieldsApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/workflowRunCellFieldsApi")>();
  return { ...original, workflowRunCellFieldsApi: { read: descriptorRead } };
});
vi.mock("@/api/workflowRunCellFieldRangeApi", async (importOriginal) => {
  const original = await importOriginal<typeof import("@/api/workflowRunCellFieldRangeApi")>();
  return { ...original, workflowRunCellFieldRangeApi: { read: rangeRead } };
});

import { WorkflowRunCellFields } from "./WorkflowRunCellFields";
import { ReceiptFooter } from "./footers/ReceiptFooter";
import type { WorkflowNodeData } from "./WorkflowNode";

const read: WorkflowRunLazyFieldRead = {
  requestedRunId: "11111111-1111-4111-8111-111111111111", scope: "LineageMerged",
  sourceRunId: "22222222-2222-4222-8222-222222222222", nodeId: "worker", iterationKey: "", observationKey: "v1",
};
const stateRecordId = "33333333-3333-4333-8333-333333333333";
const firstStartedRecordId = "44444444-4444-4444-8444-444444444444";

function descriptor(name: string): WorkflowRunCellFieldDescriptor {
  return { section: "Output", name, jsonKind: "Object", materialization: "Inline", availability: "Available",
    totalBytes: null, sha256: null, contentType: "application/json", problemCode: null };
}
function page(fields: WorkflowRunCellFieldDescriptor[], nextCursor: string | null = null): WorkflowRunCellFieldPage {
  return {
    requestedRunId: read.requestedRunId, scope: read.scope, sourceRunId: read.sourceRunId, nodeId: read.nodeId,
    iterationKey: read.iterationKey, stateRecordId, stateRecordSequence: 42, firstStartedRecordId,
    firstStartedRecordSequence: 17, status: "Success", requestCursor: null, limit: 50,
    fieldsAvailability: nextCursor ? "Truncated" : "Available", inputsAvailability: "NotRecorded",
    outputsAvailability: "Available", errorAvailability: "NotRecorded", fields, nextCursor,
  };
}
const data: WorkflowNodeData = { nodeId: "worker", typeKey: "http.request", displayName: "Request", iconKey: null,
  kind: "Regular", category: "Tools", label: null };

afterEach(() => { descriptorRead.mockReset(); rangeRead.mockReset(); });

describe("Workflow Run lazy cell fields", () => {
  it("reads zero descriptors before the footer opens and zero bytes before one exact field opens", async () => {
    descriptorRead.mockResolvedValue(page([descriptor("result")]));
    rangeRead.mockResolvedValue({
      ...read, stateRecordId, stateRecordSequence: 42, firstStartedRecordId, firstStartedRecordSequence: 17,
      section: "Output", name: "result", status: "Success", availability: "Available", source: "Inline",
      requestCursor: null, limitBytes: 64 * 1024, offsetBytes: 0, returnedBytes: 7, totalBytes: 7, nextCursor: null,
      text: "{\"x\":1}", contentType: "application/json", integrityVerified: true, completeJsonValue: true, retryable: false,
    });
    const row = { nodeId: "worker", iterationKey: "", status: "Success" as const, inputs: null, outputs: null, error: null,
      startedAt: null, completedAt: "2026-08-21T00:00:00Z", lazyFieldRead: read };
    render(<ReceiptFooter data={data} status="Success" rows={[row]} />);

    expect(descriptorRead).not.toHaveBeenCalled();
    expect(rangeRead).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: /Success/ }));
    expect(await screen.findByRole("button", { name: /Output · result/ })).toBeInTheDocument();
    expect(descriptorRead).toHaveBeenCalledExactlyOnceWith(expect.objectContaining({ requestedRunId: read.requestedRunId, nodeId: "worker" }), null, expect.any(AbortSignal));
    expect(rangeRead).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: /Output · result/ }));
    await waitFor(() => expect(rangeRead).toHaveBeenCalledTimes(1));
    expect(rangeRead.mock.calls[0][0]).toMatchObject({ requestedRunId: read.requestedRunId, sourceRunId: read.sourceRunId,
      stateRecordId, stateRecordSequence: 42, section: "Output", name: "result" });
  });

  it("does not fan one multi-row footer expansion into an N-descriptor read", async () => {
    descriptorRead.mockResolvedValue(page([descriptor("result")]));
    const lazy = (iterationKey: string) => ({ ...read, iterationKey, observationKey: iterationKey });
    const rows = ["loop#0", "loop#1"].map((iterationKey) => ({ nodeId: "worker", iterationKey, status: "Success" as const,
      inputs: null, outputs: null, error: null, startedAt: null, completedAt: "2026-08-21T00:00:00Z", lazyFieldRead: lazy(iterationKey) }));
    render(<ReceiptFooter data={data} status="Success" rows={rows} />);

    fireEvent.click(screen.getByRole("button", { name: /Success · 2 runs/ }));
    expect(descriptorRead).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("button", { name: "Inspect recorded fields for worker loop#1" }));

    await waitFor(() => expect(descriptorRead).toHaveBeenCalledTimes(1));
    expect(descriptorRead.mock.calls[0][0]).toMatchObject({ iterationKey: "loop#1" });
  });

  it("aborts stale descriptor work when a metadata poll replaces the cell observation", async () => {
    descriptorRead.mockImplementation(() => new Promise(() => {}));
    const view = render(<WorkflowRunCellFields read={read} />);
    await waitFor(() => expect(descriptorRead).toHaveBeenCalledTimes(1));
    const oldSignal = descriptorRead.mock.calls[0][2] as AbortSignal;

    view.rerender(<WorkflowRunCellFields read={{ ...read, observationKey: "v2" }} />);

    await waitFor(() => expect(descriptorRead).toHaveBeenCalledTimes(2));
    expect(oldSignal.aborted).toBe(true);
    view.unmount();
    expect((descriptorRead.mock.calls[1][2] as AbortSignal).aborted).toBe(true);
  });

  it("unmounts and aborts an expanded byte chain when a metadata poll replaces the cell observation", async () => {
    descriptorRead.mockResolvedValueOnce(page([descriptor("result")])).mockReturnValueOnce(new Promise(() => {}));
    rangeRead.mockReturnValue(new Promise(() => {}));
    const view = render(<WorkflowRunCellFields read={read} />);
    fireEvent.click(await screen.findByRole("button", { name: /Output · result/ }));
    await waitFor(() => expect(rangeRead).toHaveBeenCalledTimes(1));
    const oldRangeSignal = rangeRead.mock.calls[0][2] as AbortSignal;

    view.rerender(<WorkflowRunCellFields read={{ ...read, observationKey: "v2" }} />);

    await waitFor(() => expect(descriptorRead).toHaveBeenCalledTimes(2));
    expect(oldRangeSignal.aborted).toBe(true);
    view.unmount();
  });

  it("keeps the prior descriptor window on retryable page transport failure", async () => {
    descriptorRead.mockResolvedValueOnce(page([descriptor("first")], "next"))
      .mockRejectedValueOnce(new ApiError(503, "backend_unavailable", "offline"))
      .mockResolvedValueOnce({ ...page([descriptor("second")]), requestCursor: "next" });
    const view = render(<WorkflowRunCellFields read={read} />);
    expect(await screen.findByRole("button", { name: /Output · first/ })).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Load more fields" }));

    expect(await screen.findByText("More field metadata could not be loaded; the prior bounded window remains visible.")).toBeInTheDocument();
    expect(view.container.querySelectorAll(".wf-cell-field-descriptor")).toHaveLength(1);
    fireEvent.click(screen.getByRole("button", { name: "Retry field metadata" }));
    expect(await screen.findByRole("button", { name: /Output · second/ })).toBeInTheDocument();
  });

  it("keeps at most 512 descriptors locally and advances only by an explicit opaque-cursor action", async () => {
    descriptorRead.mockImplementation((_coordinate, cursor: string | null) => {
      const index = cursor === null ? 0 : Number(cursor.slice(1));
      const fields = Array.from({ length: 50 }, (_, item) => descriptor(`p${index}-${item}`));
      const next = index < 10 ? `c${index + 1}` : null;
      return Promise.resolve({ ...page(fields, next), requestCursor: cursor });
    });
    const view = render(<WorkflowRunCellFields read={read} />);
    await waitFor(() => expect(view.container.querySelectorAll(".wf-cell-field-descriptor")).toHaveLength(50));
    expect(descriptorRead).toHaveBeenCalledTimes(1);

    for (let index = 1; index <= 10; index += 1) {
      fireEvent.click(screen.getByRole("button", { name: "Load more fields" }));
      await waitFor(() => expect(descriptorRead).toHaveBeenCalledTimes(index + 1));
    }

    await waitFor(() => expect(view.container.querySelectorAll(".wf-cell-field-descriptor")).toHaveLength(512));
    expect(screen.getByText("Earlier field descriptors were omitted from this 512-item local window.")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Return to first fields" }));
    await waitFor(() => expect(descriptorRead).toHaveBeenCalledTimes(12));
    await waitFor(() => expect(view.container.querySelectorAll(".wf-cell-field-descriptor")).toHaveLength(50));
    expect(rangeRead).not.toHaveBeenCalled();
  });
});
