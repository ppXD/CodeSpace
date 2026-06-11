import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { WorkflowRunDetail, WorkflowRunNodeSummary } from "@/api/workflows";
import { RunDetailView } from "./RunDetailView";

/**
 * RunDetailView's sub-workflow drill-down. A flow.subworkflow step in the node trace carries the
 * child run it spawned (node.childRunId). These pin the generic "no jumping around" behaviour:
 *   1. the step id becomes a link that opens the child run (onOpenRun);
 *   2. the child run-detail embeds inline, but only once expanded (no eager polling for N steps);
 *   3. with no navigation handler the id is plain text; a non-subworkflow node shows neither.
 */
const { useWorkflowRunMock, useAgentRunMock } = vi.hoisted(() => ({
  useWorkflowRunMock: vi.fn(),
  useAgentRunMock: vi.fn<(id?: string) => { data: { status: string } | undefined }>(() => ({ data: undefined })),
}));

vi.mock("@/hooks/use-workflows", () => ({
  useResumeRun: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  useWorkflowRun: (runId: string) => useWorkflowRunMock(runId),
}));

// RunNodeRow reads the agent run's live status for its badge (and AgentRunTimeline streams it); mock the
// hooks so the row renders without a QueryClient. Default: no agent run → badge falls back to node status.
vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: (id?: string) => useAgentRunMock(id),
  useAgentRunEvents: () => ({ data: [] }),
}));

function node(over: Partial<WorkflowRunNodeSummary> & { nodeId: string }): WorkflowRunNodeSummary {
  return { iterationKey: "", status: "Success", inputs: {}, outputs: {}, error: null, startedAt: null, completedAt: null, childRunId: null, ...over };
}

function detail(over: Partial<WorkflowRunDetail>): WorkflowRunDetail {
  return {
    id: "parent-1", workflowId: "w", workflowVersion: 1, sourceType: "manual",
    normalizedPayload: {}, status: "Success", error: null, startedAt: null, completedAt: null,
    nodes: [], outputs: {}, pendingWait: null, ...over,
  };
}

const ok = (data: WorkflowRunDetail) => ({ isLoading: false, error: null, data });
const missing = { isLoading: false, error: null, data: null };

beforeEach(() => {
  useWorkflowRunMock.mockReset();
  useAgentRunMock.mockReset();
  useAgentRunMock.mockImplementation(() => ({ data: undefined }));
});

describe("RunDetailView — sub-workflow step drill-down", () => {
  it("links the step to its child run, and embeds the child inline only once expanded", () => {
    useWorkflowRunMock.mockImplementation((runId: string) => {
      if (runId === "parent-1") return ok(detail({ nodes: [node({ nodeId: "start" }), node({ nodeId: "sub", childRunId: "child-1" })] }));
      if (runId === "child-1") return ok(detail({ id: "child-1", nodes: [node({ nodeId: "child-step" })] }));
      return missing;
    });
    const onOpenRun = vi.fn();

    render(<RunDetailView runId="parent-1" onOpenRun={onOpenRun} />);

    // The sub-workflow step id is a link → opens the child run full-page.
    fireEvent.click(screen.getByTitle("Open the sub-workflow run"));
    expect(onOpenRun).toHaveBeenCalledWith("child-1");

    // Collapsed by default — the child run-detail is NOT fetched yet (so N steps cost no polling)…
    expect(useWorkflowRunMock).not.toHaveBeenCalledWith("child-1");

    // …expanding mounts the live child run-detail inline, with its own step trace.
    fireEvent.click(screen.getByText("Sub-workflow run"));
    expect(useWorkflowRunMock).toHaveBeenCalledWith("child-1");
    expect(screen.getByText("child-step")).toBeTruthy();
  });

  it("renders the step id as plain text when no navigation handler is given", () => {
    useWorkflowRunMock.mockImplementation((runId: string) =>
      runId === "parent-1" ? ok(detail({ nodes: [node({ nodeId: "sub", childRunId: "child-1" })] })) : missing);

    render(<RunDetailView runId="parent-1" />);

    expect(screen.queryByTitle("Open the sub-workflow run")).toBeNull();
    expect(screen.getByText("sub")).toBeTruthy(); // still shown — just not a link
  });

  it("shows no child embed for a non-subworkflow node", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({ nodes: [node({ nodeId: "start" })] })));

    render(<RunDetailView runId="parent-1" onOpenRun={vi.fn()} />);

    expect(screen.queryByText("Sub-workflow run")).toBeNull();
    expect(screen.queryByTitle("Open the sub-workflow run")).toBeNull();
  });

  it("does not double-embed the child the run is suspended on (the suspended panel already shows it)", () => {
    useWorkflowRunMock.mockImplementation((runId: string) => {
      if (runId === "parent-1") return ok(detail({
        status: "Suspended",
        pendingWait: { nodeId: "sub", kind: "Subworkflow", token: "child-1", payload: {} },
        nodes: [node({ nodeId: "sub", status: "Suspended", childRunId: "child-1" })],
      }));
      if (runId === "child-1") return ok(detail({ id: "child-1", nodes: [node({ nodeId: "child-step" })] }));
      return missing;
    });

    render(<RunDetailView runId="parent-1" />);

    // The suspended panel embeds the child at the top…
    expect(screen.getByText("Running a sub-workflow")).toBeTruthy();
    // …so the trace row must NOT offer the same child again (no duplicate embed / double-poll).
    expect(screen.queryByText("Sub-workflow run")).toBeNull();
  });
});

describe("RunDetailView — live agent-code node badge", () => {
  const parkedTitle = "Workflow node is parked (Suspended) while its agent runs";

  it("badges a Suspended agent.code node with its agent run's LIVE status, not 'Suspended'", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      status: "Suspended",
      nodes: [node({ nodeId: "code", status: "Suspended", agentRunId: "ar-1" })],
    })));
    useAgentRunMock.mockImplementation((id?: string) => ({ data: id === "ar-1" ? { status: "Running" } : undefined }));

    render(<RunDetailView runId="parent-1" />);

    // The row's status badge reads "Running" (derived from the agent run), with the engine truth on hover.
    const badge = screen.getByTitle(parkedTitle);
    expect(badge.textContent).toBe("Running");
  });

  it("keeps the node's own status for a Suspended node with no agent run (e.g. a Timer wait)", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      status: "Suspended",
      nodes: [node({ nodeId: "sleep", status: "Suspended" })],  // no agentRunId
    })));

    const { container } = render(<RunDetailView runId="parent-1" />);

    expect(screen.queryByTitle(parkedTitle)).toBeNull();
    // The node row's own pill keeps "Suspended" (scoped, so it's not the run-summary badge).
    expect(container.querySelector(".wf-run-node .wf-status-pill")?.textContent).toBe("Suspended");
  });

  it("does NOT override the badge once the agent run is terminal (the node is about to resume)", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      status: "Suspended",
      nodes: [node({ nodeId: "code", status: "Suspended", agentRunId: "ar-1" })],
    })));
    useAgentRunMock.mockImplementation((id?: string) => ({ data: id === "ar-1" ? { status: "Failed" } : undefined }));

    render(<RunDetailView runId="parent-1" />);

    // Terminal agent status → keep the node's own status (no parked-badge override).
    expect(screen.queryByTitle(parkedTitle)).toBeNull();
  });
});

describe("RunDetailView — parallel-wave observability", () => {
  const at = (sec: number) => new Date(Date.UTC(2026, 0, 1, 0, 0, sec)).toISOString();

  it("badges nodes whose execution overlapped in time (ran concurrently)", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      nodes: [
        node({ nodeId: "a", startedAt: at(0), completedAt: at(10) }),
        node({ nodeId: "b", startedAt: at(2), completedAt: at(8) }),  // overlaps a
      ],
    })));

    render(<RunDetailView runId="parent-1" />);
    expect(screen.getAllByText("∥ parallel").length).toBe(2);
  });

  it("shows no parallel badge for a strictly sequential run", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      nodes: [
        node({ nodeId: "a", startedAt: at(0), completedAt: at(5) }),
        node({ nodeId: "b", startedAt: at(5), completedAt: at(10) }), // touching handoff, not overlap
      ],
    })));

    render(<RunDetailView runId="parent-1" />);
    expect(screen.queryByText("∥ parallel")).toBeNull();
  });
});
