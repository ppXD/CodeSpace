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
const { useWorkflowRunMock, useAgentRunMock, useRunPhasesMock } = vi.hoisted(() => ({
  useWorkflowRunMock: vi.fn(),
  useAgentRunMock: vi.fn<(id?: string) => { data: { status: string } | undefined }>(() => ({ data: undefined })),
  useRunPhasesMock: vi.fn<() => { data: { phases: unknown[] } | undefined }>(() => ({ data: undefined })),
}));

vi.mock("@/hooks/use-workflows", () => ({
  useResumeRun: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  useWorkflowRun: (runId: string) => useWorkflowRunMock(runId),
  useWorkflow: () => ({ data: undefined, isLoading: false }),
  useNodeManifests: () => ({ data: [] }),
  useRunPhases: () => useRunPhasesMock(),
  useRunTimeline: () => ({ data: undefined }),   // the narrative band stays empty in these node-trace tests
}));

// RunNodeRow reads the agent run's live status for its badge (and AgentRunTimeline streams it); mock the
// hooks so the row renders without a QueryClient. Default: no agent run → badge falls back to node status.
// AgentToolCalls (embedded peer of the timeline) also reads useToolCalls — default to an empty audit.
vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: (id?: string) => useAgentRunMock(id),
  useAgentRunEvents: () => ({ data: [] }),
  useToolCalls: () => ({ data: [], isLoading: false }),
}));

// AgentToolCalls resolves an approver id → name via the member-identity map; no approver in these tests.
vi.mock("@/hooks/use-team-members", () => ({
  useTeamMemberIdentityMap: () => new Map(),
}));

function node(over: Partial<WorkflowRunNodeSummary> & { nodeId: string }): WorkflowRunNodeSummary {
  return { iterationKey: "", containerKind: null, status: "Success", inputs: {}, outputs: {}, error: null, startedAt: null, completedAt: null, childRunId: null, ...over };
}

function detail(over: Partial<WorkflowRunDetail>): WorkflowRunDetail {
  return {
    id: "parent-1", workflowId: "w", workflowVersion: 1, sourceType: "manual",
    normalizedPayload: {}, status: "Success", error: null, startedAt: null, completedAt: null,
    createdDate: "2026-06-22T00:00:00Z", nodes: [], outputs: {}, pendingWait: null, ...over,
  };
}

const ok = (data: WorkflowRunDetail) => ({ isLoading: false, error: null, data });
const missing = { isLoading: false, error: null, data: null };

beforeEach(() => {
  useWorkflowRunMock.mockReset();
  useAgentRunMock.mockReset();
  useAgentRunMock.mockImplementation(() => ({ data: undefined }));
  useRunPhasesMock.mockReset();
  useRunPhasesMock.mockReturnValue({ data: undefined });   // no phases → no Live-work, node trace stays primary
});

const phasesWithAgent = { data: { phases: [{ id: "p", label: "Implement", kind: "agent", status: "Active", order: 0, agents: [{ agentRunId: "ar1", status: "Running", label: "backend-fix" }], metrics: { agentCount: 1, succeededCount: 0, failedCount: 0 }, sourceKey: "supervisor-ledger" }] } };

describe("RunDetailView — sub-workflow step drill-down", () => {
  it("links the step to its child run, and embeds the child inline only once expanded", () => {
    useWorkflowRunMock.mockImplementation((runId: string) => {
      if (runId === "parent-1") return ok(detail({ nodes: [node({ nodeId: "start" }), node({ nodeId: "sub", childRunId: "child-1" })] }));
      if (runId === "child-1") return ok(detail({ id: "child-1", nodes: [node({ nodeId: "child-step" })] }));
      return missing;
    });
    const onOpenRun = vi.fn();

    render(<RunDetailView defaultView="activity" runId="parent-1" onOpenRun={onOpenRun} />);

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

    render(<RunDetailView defaultView="activity" runId="parent-1" />);

    expect(screen.queryByTitle("Open the sub-workflow run")).toBeNull();
    expect(screen.getByText("sub")).toBeTruthy(); // still shown — just not a link
  });

  it("shows no child embed for a non-subworkflow node", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({ nodes: [node({ nodeId: "start" })] })));

    render(<RunDetailView defaultView="activity" runId="parent-1" onOpenRun={vi.fn()} />);

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

    render(<RunDetailView defaultView="activity" runId="parent-1" />);

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

    render(<RunDetailView defaultView="activity" runId="parent-1" />);

    // The row's status badge reads "Running" (derived from the agent run), with the engine truth on hover.
    const badge = screen.getByTitle(parkedTitle);
    expect(badge.textContent).toBe("Running");
  });

  it("keeps the node's own status for a Suspended node with no agent run (e.g. a Timer wait)", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      status: "Suspended",
      nodes: [node({ nodeId: "sleep", status: "Suspended" })],  // no agentRunId
    })));

    const { container } = render(<RunDetailView defaultView="activity" runId="parent-1" />);

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

    render(<RunDetailView defaultView="activity" runId="parent-1" />);

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

    render(<RunDetailView defaultView="activity" runId="parent-1" />);
    expect(screen.getAllByText("∥ parallel").length).toBe(2);
  });

  it("shows no parallel badge for a strictly sequential run", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      nodes: [
        node({ nodeId: "a", startedAt: at(0), completedAt: at(5) }),
        node({ nodeId: "b", startedAt: at(5), completedAt: at(10) }), // touching handoff, not overlap
      ],
    })));

    render(<RunDetailView defaultView="activity" runId="parent-1" />);
    expect(screen.queryByText("∥ parallel")).toBeNull();
  });
});

describe("RunDetailView — map-branch observability", () => {
  // A flow.map element-branch body row — the backend stamps containerKind = "flow.map".
  const mapNode = (over: Partial<WorkflowRunNodeSummary> & { nodeId: string }) => node({ containerKind: "flow.map", ...over });

  it("groups + badges a K-branch map run (per-element badge + per-map rollup)", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      nodes: [
        node({ nodeId: "synth", iterationKey: "" }),                             // a top-level (non-branch) node
        mapNode({ nodeId: "work", iterationKey: "map#0" }),
        mapNode({ nodeId: "work", iterationKey: "map#1" }),
        mapNode({ nodeId: "work", iterationKey: "map#2", status: "Failure" }),   // one branch failed
      ],
    })));

    render(<RunDetailView defaultView="activity" runId="parent-1" />);

    // Per-element branch badges — three distinct elements of `map`.
    expect(screen.getByText("#0")).toBeTruthy();
    expect(screen.getByText("#1")).toBeTruthy();
    expect(screen.getByText("#2")).toBeTruthy();

    // Per-map rollup chip — 2/3 done + 1 failed.
    expect(screen.getByText("map")).toBeTruthy();
    expect(screen.getByText("2/3 done")).toBeTruthy();
    expect(screen.getByText("1 failed")).toBeTruthy();
  });

  it("badges a nested map-in-map branch as #i/#j", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      nodes: [mapNode({ nodeId: "leaf", iterationKey: "outer#1/inner#2" })],
    })));

    render(<RunDetailView defaultView="activity" runId="parent-1" />);
    expect(screen.getByText("#1/#2")).toBeTruthy();
  });

  it("renders a LOOP run exactly as before — no branch badges, no rollup (same key shape, but containerKind is flow.loop)", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      nodes: [
        node({ nodeId: "step", iterationKey: "loop#0", containerKind: "flow.loop" }),
        node({ nodeId: "step", iterationKey: "loop#1", containerKind: "flow.loop" }),
      ],
    })));

    const { container } = render(<RunDetailView defaultView="activity" runId="parent-1" />);
    expect(container.querySelector(".wf-run-node-branch")).toBeNull();
    expect(container.querySelector(".wf-map-rollups")).toBeNull();
  });

  it("renders a non-map run exactly as before — no branch badges, no rollup", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      nodes: [node({ nodeId: "a" }), node({ nodeId: "b" })],   // empty iteration keys (default)
    })));

    const { container } = render(<RunDetailView defaultView="activity" runId="parent-1" />);
    expect(container.querySelector(".wf-run-node-branch")).toBeNull();
    expect(container.querySelector(".wf-map-rollups")).toBeNull();
  });
});

describe("RunDetailView — run-view tabs", () => {
  beforeEach(() => useWorkflowRunMock.mockImplementation(() => ok(detail({ nodes: [node({ nodeId: "a" })] }))));

  it("offers the four run views, Activity first", () => {
    render(<RunDetailView runId="parent-1" />);
    for (const t of ["Activity", "Canvas", "Changes", "Trace"]) {
      expect(screen.getByRole("tab", { name: t })).toBeInTheDocument();
    }
    expect(screen.getByRole("tab", { name: "Activity" })).toHaveAttribute("aria-selected", "true");
  });

  it("defaults to the Activity narrative (the node trace), not a backend-blocked tab", () => {
    render(<RunDetailView runId="parent-1" />);
    expect(screen.getByText("Node execution")).toBeInTheDocument();
    expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();
  });

  it("shows an honest 'coming soon' placeholder for Changes / Trace (no fake-empty panel)", () => {
    render(<RunDetailView runId="parent-1" />);

    fireEvent.click(screen.getByRole("tab", { name: "Changes" }));
    expect(screen.getByText("Coming soon")).toBeInTheDocument();
    expect(screen.queryByText("Node execution")).not.toBeInTheDocument();   // narrative is hidden behind the tab

    fireEvent.click(screen.getByRole("tab", { name: "Trace" }));
    expect(screen.getByText("Coming soon")).toBeInTheDocument();
  });

  it("hides the tab bar when embedded (nested), so the editor dialog's child runs stay plain", () => {
    render(<RunDetailView runId="parent-1" nested />);
    expect(screen.queryByRole("tab", { name: "Activity" })).not.toBeInTheDocument();
  });

  it("drops the redundant summary line in the framed panel (tab strip is the head, aligning with the rails)", () => {
    const { container } = render(<RunDetailView runId="parent-1" />);   // non-nested = the Run Room panel
    expect(container.querySelector(".wf-run-summary")).toBeNull();      // metadata now lives in the page header + Run rail
    expect(screen.getByRole("tablist")).toBeInTheDocument();
  });

  it("keeps the compact summary line when nested (the editor dialog has no header/rails)", () => {
    const { container } = render(<RunDetailView runId="parent-1" nested />);
    expect(container.querySelector(".wf-run-summary")).not.toBeNull();
  });
});

describe("RunDetailView — Live-work center", () => {
  it("shows the agent cards and FOLDS the raw node trace when the run has agents", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({ nodes: [node({ nodeId: "code", agentRunId: "ar1" })] })));
    useRunPhasesMock.mockReturnValue(phasesWithAgent);

    render(<RunDetailView runId="parent-1" />);

    expect(screen.getByText("backend-fix")).toBeInTheDocument();        // the Live-work agent card
    expect(screen.getByText("Workflow nodes")).toBeInTheDocument();     // the node trace is now a fold
    expect(screen.queryByText("Node execution")).not.toBeInTheDocument(); // …lazy, so unmounted while collapsed
  });

  it("keeps the node trace primary when the run has no agents (a structural workflow)", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({ nodes: [node({ nodeId: "start" })] })));
    useRunPhasesMock.mockReturnValue({ data: { phases: [] } });

    render(<RunDetailView runId="parent-1" />);

    expect(screen.getByText("Node execution")).toBeInTheDocument();     // primary, not folded
    expect(screen.queryByText("Workflow nodes")).not.toBeInTheDocument();
  });
});
