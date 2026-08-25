import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { workflowsApi, type WorkflowRunModelCallDetailMetadata, type WorkflowRunModelCallPage } from "@/api/workflows";
import { ModelCallsPanel } from "./ModelCallsPanel";

const runId = "11111111-1111-1111-1111-111111111111";
const callId = "22222222-2222-2222-2222-222222222222";

const page: WorkflowRunModelCallPage = {
  runId,
  requestCursor: null,
  limit: 100,
  nextCursor: null,
  items: [{
    workflowRunModelCallId: callId,
    runId,
    callOrdinal: 1,
    nodeId: "agent",
    iterationKey: "agent#0",
    executionAttemptId: null,
    purpose: "agent.model-call",
    requestedProvider: "OpenAI",
    requestedModel: "gpt-5",
    captureSource: "agent-run-record/v1",
    captureCompleteness: "Exact",
    createdAt: "2026-08-21T01:00:00Z",
  }],
};

afterEach(() => vi.restoreAllMocks());

describe("ModelCallsPanel", () => {
  it("leaves the loading state when the run becomes inaccessible", async () => {
    vi.spyOn(workflowsApi, "pageRunModelCalls").mockResolvedValue(null);

    render(<ModelCallsPanel runId={runId} active={false} />);

    expect(await screen.findByText("This run is no longer available.")).toBeTruthy();
    expect(screen.queryByText("Loading model calls…")).toBeNull();
  });

  it("shows a terminal unavailable state when selected metadata disappears", async () => {
    vi.spyOn(workflowsApi, "pageRunModelCalls").mockResolvedValue(page);
    vi.spyOn(workflowsApi, "getRunModelCallById").mockResolvedValue(null);

    render(<ModelCallsPanel runId={runId} active={false} />);
    fireEvent.click(await screen.findByRole("button", { name: /agent\.model-call/i }));

    await waitFor(() => expect(screen.getByText("Selected model call is unavailable.")).toBeTruthy());
    expect(screen.queryByText("Loading selected call…")).toBeNull();
  });

  it("renders bounded detail metadata without requesting any body", async () => {
    vi.spyOn(workflowsApi, "pageRunModelCalls").mockResolvedValue(page);
    const detail = {
      ...page.items[0],
      schemaVersion: 1,
      bodies: [],
      attempts: [],
    } as WorkflowRunModelCallDetailMetadata;
    vi.spyOn(workflowsApi, "getRunModelCallById").mockResolvedValue(detail);
    const bodySpy = vi.spyOn(workflowsApi, "getRunModelCallBody");

    render(<ModelCallsPanel runId={runId} active={false} />);
    fireEvent.click(await screen.findByRole("button", { name: /agent\.model-call/i }));

    expect(await screen.findByText("Stable call id")).toBeTruthy();
    expect(screen.getByText(callId)).toBeTruthy();
    expect(bodySpy).not.toHaveBeenCalled();
  });

  it("keeps refreshing pending capture metadata after the workflow is terminal", async () => {
    vi.spyOn(workflowsApi, "pageRunModelCalls").mockResolvedValue(page);
    const pending = {
      ...page.items[0],
      schemaVersion: 1,
      bodies: [{ body: "LogicalRequest", artifactId: null, referenceState: "Referenced", captureCompleteness: "Exact", captureHealth: "Pending" }],
      attempts: [],
    } as WorkflowRunModelCallDetailMetadata;
    const available = {
      ...pending,
      bodies: [{ ...pending.bodies[0], captureHealth: "Available" }],
    } as WorkflowRunModelCallDetailMetadata;
    const detailSpy = vi.spyOn(workflowsApi, "getRunModelCallById").mockResolvedValueOnce(pending).mockResolvedValueOnce(available);
    vi.spyOn(window, "setInterval").mockImplementation((handler: TimerHandler) => {
      queueMicrotask(() => { if (typeof handler === "function") handler(); });
      return 1;
    });

    render(<ModelCallsPanel runId={runId} active={false} />);
    fireEvent.click(await screen.findByRole("button", { name: /agent\.model-call/i }));
    await screen.findByText("Stable call id");

    await waitFor(() => expect(detailSpy).toHaveBeenCalledTimes(2));
  });
});
