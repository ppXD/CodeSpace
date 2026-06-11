import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { WorkflowRunWaitInfo } from "@/api/workflows";
import { SuspendedPanel } from "./RunDetailView";

/**
 * SuspendedPanel is the resume affordance shown on a Suspended run. These pin the behaviour the
 * human-in-the-loop flow depends on:
 *   1. an Approval wait renders the prompt + Approve/Reject;
 *   2. Approve / Reject post the correct decision (with the optional comment);
 *   3. a Timer wait shows a wake hint, NOT approval buttons.
 */

const { mutate, useWorkflowRunMock } = vi.hoisted(() => ({ mutate: vi.fn(), useWorkflowRunMock: vi.fn() }));

vi.mock("@/hooks/use-workflows", () => ({
  useResumeRun: () => ({ mutate, isPending: false, isError: false }),
  // Used by the embedded child RunDetailView for a Subworkflow wait.
  useWorkflowRun: (runId: string) => useWorkflowRunMock(runId),
}));

// The embedded child RunDetailView's node rows read each node's agent-run status for their badge; mock the
// hooks so they render without a QueryClient (these child nodes carry no agent run → no-op).
vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => ({ data: undefined }),
  useAgentRunEvents: () => ({ data: [] }),
}));

function approvalWait(prompt: string): WorkflowRunWaitInfo {
  return { nodeId: "approval", kind: "Approval", token: "tok-1", payload: { prompt } };
}

describe("SuspendedPanel", () => {
  it("approves with the typed comment", () => {
    render(<SuspendedPanel runId="run-1" wait={approvalWait("Deploy to production?")} />);

    expect(screen.getByText("Deploy to production?")).toBeTruthy();

    fireEvent.change(screen.getByPlaceholderText("Comment (optional)"), { target: { value: "  ship it  " } });
    fireEvent.click(screen.getByText("Approve"));

    expect(mutate).toHaveBeenCalledWith({ approved: true, comment: "ship it" });
  });

  it("rejects, and omits an empty comment", () => {
    mutate.mockClear();
    render(<SuspendedPanel runId="run-1" wait={approvalWait("OK?")} />);

    fireEvent.click(screen.getByText("Reject"));

    expect(mutate).toHaveBeenCalledWith({ approved: false, comment: undefined });
  });

  it("shows a wake hint for a Timer wait — no approval buttons", () => {
    const wait: WorkflowRunWaitInfo = { nodeId: "sleep", kind: "Timer", token: "tok-2", wakeAt: "2026-05-30T11:00:00Z", payload: {} };
    render(<SuspendedPanel runId="run-1" wait={wait} />);

    expect(screen.queryByText("Approve")).toBeNull();
    expect(screen.getByText(/Resumes around/)).toBeTruthy();
  });

  it("shows the tokened callback URL for a Callback wait — no approval buttons", () => {
    const wait: WorkflowRunWaitInfo = { nodeId: "cb", kind: "Callback", token: "abc123", payload: {} };
    render(<SuspendedPanel runId="run-1" wait={wait} />);

    expect(screen.queryByText("Approve")).toBeNull();
    const url = screen.getByDisplayValue(/\/api\/workflows\/callbacks\/abc123$/);
    expect(url).toBeTruthy();
  });

  it("embeds the live child run for a Subworkflow wait, with its approval operable inline", () => {
    mutate.mockClear();
    // The child run is itself suspended on its own approval.
    useWorkflowRunMock.mockReturnValue({
      isLoading: false,
      error: null,
      data: {
        id: "child-1", workflowId: "w", workflowVersion: 1, sourceType: "workflow.child",
        normalizedPayload: {}, status: "Suspended", error: null, startedAt: null, completedAt: null,
        nodes: [{ nodeId: "review", iterationKey: "", status: "Suspended", inputs: {}, outputs: {}, error: null, startedAt: null, completedAt: null }],
        pendingWait: { nodeId: "review", kind: "Approval", token: "ctok", payload: { prompt: "Approve the review?" } },
      },
    });

    render(<SuspendedPanel runId="parent-1" wait={{ nodeId: "sub", kind: "Subworkflow", token: "child-1", payload: {} }} />);

    // The embedded child's content is visible…
    expect(screen.getByText("review")).toBeTruthy();              // a child node id
    expect(screen.getByText("Approve the review?")).toBeTruthy(); // the child's approval prompt
    // …and approving the child resolves the CHILD run (which the engine then turns into a parent resume).
    fireEvent.click(screen.getByText("Approve"));
    expect(mutate).toHaveBeenCalledWith({ approved: true, comment: undefined });
  });

  it("stops embedding once nesting is too deep — shows the child run id instead of fetching", () => {
    useWorkflowRunMock.mockClear();

    render(<SuspendedPanel runId="p" wait={{ nodeId: "sub", kind: "Subworkflow", token: "deep-child", payload: {} }} depth={3} />);

    expect(screen.getByText("deep-child")).toBeTruthy();
    expect(useWorkflowRunMock).not.toHaveBeenCalled();
  });
});
