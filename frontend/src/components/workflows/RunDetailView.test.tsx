import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { WorkflowRunDetail, WorkflowRunNodeSummary } from "@/api/workflows";
import type { WorkflowRunViewMetadata } from "@/api/workflowRunViewMetadataApi";
import { RunDetailView } from "./RunDetailView";

/**
 * RunDetailView's sub-workflow drill-down. A flow.subworkflow step in the node trace carries the
 * child run it spawned (node.childRunId). These pin the generic "no jumping around" behaviour:
 *   1. the step id becomes a link that opens the child run (onOpenRun);
 *   2. the child run-detail embeds inline, but only once expanded (no eager polling for N steps);
 *   3. with no navigation handler the id is plain text; a non-subworkflow node shows neither.
 */
const { useWorkflowRunMock, useWorkflowRunDetailMock, useWorkflowRunPendingWaitMock, useWorkflowRunIdentityMock, useWorkflowRunViewMetadataMock, useNodeManifestsMock, useAgentRunMock, useRunPhasesMock, governedToolsPanelMock, runCanvasMock } = vi.hoisted(() => ({
  useWorkflowRunMock: vi.fn(),
  useWorkflowRunDetailMock: vi.fn(),
  useWorkflowRunPendingWaitMock: vi.fn(),
  useWorkflowRunIdentityMock: vi.fn(),
  useWorkflowRunViewMetadataMock: vi.fn(),
  useNodeManifestsMock: vi.fn(),
  useAgentRunMock: vi.fn<(id?: string) => { data: { status: string } | undefined }>(() => ({ data: undefined })),
  useRunPhasesMock: vi.fn<() => { data: { phases: unknown[] } | undefined; isLoading?: boolean }>(() => ({ data: undefined })),
  governedToolsPanelMock: vi.fn(),
  runCanvasMock: vi.fn(),
}));

vi.mock("@/hooks/use-workflows", () => ({
  useResumeRun: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  isRunActive: (s: string) => !["Success", "Failure", "Cancelled"].includes(s),
  useWorkflowRun: (runId: string) => useWorkflowRunMock(runId),
  useWorkflowRunDetail: (runId: string, enabled = true) => useWorkflowRunDetailMock(runId, enabled),
  useWorkflowRunPendingWait: (runId: string, enabled = true) => useWorkflowRunPendingWaitMock(runId, enabled),
  useWorkflowRunIdentity: (runId: string, enabled = true) => useWorkflowRunIdentityMock(runId, enabled),
  useWorkflow: () => ({ data: undefined, isLoading: false }),
  useNodeManifests: () => useNodeManifestsMock(),
  useRunPhases: () => useRunPhasesMock(),
  useRunTimeline: () => ({ data: undefined }),   // the narrative band stays empty in these node-trace tests
  useRunRecordWindow: () => ({ records: [], isLoading: false, isLoadingOlder: false, error: null, hasOlder: false, olderRecordsOmitted: false, newerRecordsOmitted: false, atLatest: true, loadOlder: vi.fn(), returnToLatest: vi.fn() }),   // the Trace tab isn't the active view here
  useRunDataCompleteness: () => ({ data: { runId: "parent-1", scope: "RecordedFacetsOnly", facets: [], hasStatements: false, runWideVerdict: null, truncated: false }, isLoading: false, error: null }),
  useCellAttempts: () => ({ data: { attempts: [] } }),   // a terminal's per-cell history — empty (no rerun) here
}));

vi.mock("@/hooks/use-workflow-run-view-metadata", () => ({
  useWorkflowRunViewMetadata: (runId: string, enabled = true) => useWorkflowRunViewMetadataMock(runId, enabled),
}));

// RunNodeRow reads the agent run's live status for its badge (and AgentRunTimeline streams it); mock the
// hooks so the row renders without a QueryClient. Default: no agent run → badge falls back to node status.
// AgentToolCalls (embedded peer of the timeline) also reads the governed window — default to an exact empty audit.
vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: (id?: string) => useAgentRunMock(id),
  useAgentRunEventPreview: () => ({ data: [] }),
  useAgentRunEventWindow: () => ({ data: [], isLoading: false, isLoadingOlder: false, error: null, hasOlder: false, olderEventsOmitted: false, newerEventsOmitted: false, atLatest: true, loadOlder: vi.fn(), returnToLatest: vi.fn() }),
  useToolCallWindow: () => ({ data: [], hasLoaded: true, isLoading: false, isLoadingOlder: false, error: null, hasOlder: false, olderItemsOmitted: false, newerItemsOmitted: false, atLatest: true, loadOlder: vi.fn(), returnToLatest: vi.fn() }),
}));

// AgentToolCalls resolves an approver id → name via the member-identity map; no approver in these tests.
vi.mock("@/hooks/use-team-members", () => ({
  useTeamMemberIdentityMap: () => new Map(),
}));

vi.mock("./GovernedToolsPanel", () => ({
  GovernedToolsPanel: ({ runId, active }: { runId: string; active: boolean }) => {
    governedToolsPanelMock({ runId, active });
    return <div data-testid="governed-tools-panel" data-run-id={runId} data-active={active} />;
  },
}));

vi.mock("./RunCanvas", () => ({
  RunCanvas: (props: { runNodes: WorkflowRunNodeSummary[]; runStatus: string; onOpenRun?: (runId: string) => void }) => {
    runCanvasMock(props);
    const childRunId = props.runNodes.find((row) => row.childRunId)?.childRunId;
    return <button type="button" data-testid="bounded-run-canvas" data-status={props.runStatus} onClick={() => childRunId && props.onOpenRun?.(childRunId)}>Canvas</button>;
  },
}));

function node(over: Partial<WorkflowRunNodeSummary> & { nodeId: string }): WorkflowRunNodeSummary {
  return { iterationKey: "", containerKind: null, status: "Success", inputs: {}, outputs: {}, error: null, startedAt: null, completedAt: null, childRunId: null, ...over };
}

function detail(over: Partial<WorkflowRunDetail>): WorkflowRunDetail {
  return {
    id: "parent-1", runNumber: 1, workflowId: "w", workflowVersion: 1, sourceType: "manual",
    normalizedPayload: {}, status: "Success", error: null, startedAt: null, completedAt: null,
    createdDate: "2026-06-22T00:00:00Z", nodes: [], outputs: {}, pendingWait: null, ...over,
  };
}

function viewMetadata(over: Partial<WorkflowRunViewMetadata> = {}): WorkflowRunViewMetadata {
  return {
    runId: "parent-1", runNumber: 1, workflowId: null, workflowVersion: 1, sourceType: "manual", parentRunId: null,
    status: "Success", hasError: false, startedAt: null, completedAt: null, createdDate: "2026-06-22T00:00:00Z",
    scope: "LineageMerged", cellsAvailability: "Available", linksAvailability: "Available", cells: [],
    topologyAvailability: "Available", topology: { nodes: [], edges: [] }, ...over,
  };
}

const ok = (data: WorkflowRunDetail) => ({ isLoading: false, error: null, data });
const missing = { isLoading: false, error: null, data: null };

beforeEach(() => {
  useWorkflowRunMock.mockReset();
  useWorkflowRunDetailMock.mockReset();
  useWorkflowRunDetailMock.mockImplementation((runId: string) => useWorkflowRunMock(runId));
  useWorkflowRunPendingWaitMock.mockReset();
  useWorkflowRunPendingWaitMock.mockReturnValue({ isLoading: false, error: null, data: { runId: "parent-1", wait: null } });
  useWorkflowRunIdentityMock.mockReset();
  useWorkflowRunIdentityMock.mockReturnValue({ isLoading: false, error: null, data: { id: "parent-1", runNumber: 1, status: "Success" } });
  useWorkflowRunViewMetadataMock.mockReset();
  useWorkflowRunViewMetadataMock.mockReturnValue({ isLoading: false, error: null, data: viewMetadata() });
  useNodeManifestsMock.mockReset();
  useNodeManifestsMock.mockReturnValue({ data: [] });
  useAgentRunMock.mockReset();
  useAgentRunMock.mockImplementation(() => ({ data: undefined }));
  useRunPhasesMock.mockReset();
  useRunPhasesMock.mockReturnValue({ data: undefined });   // no phases → no Live-work, node trace stays primary
  governedToolsPanelMock.mockReset();
  runCanvasMock.mockReset();
});

const phasesWithAgent = { data: { phases: [{ id: "p", label: "Implement", kind: "agent", status: "Active", order: 0, agents: [{ agentRunId: "ar1", status: "Running", label: "backend-fix" }], metrics: { agentCount: 1, succeededCount: 0, failedCount: 0 }, sourceKey: "supervisor-ledger" }] } };
const openWorkflowNodes = () => fireEvent.click(screen.getByText("Workflow nodes"));

describe("RunDetailView — sub-workflow step drill-down", () => {
  it("links the step to its child run, and embeds the child inline only once expanded", () => {
    useWorkflowRunMock.mockImplementation((runId: string) => {
      if (runId === "parent-1") return ok(detail({ nodes: [node({ nodeId: "start" }), node({ nodeId: "sub", childRunId: "child-1" })] }));
      if (runId === "child-1") return ok(detail({ id: "child-1", nodes: [node({ nodeId: "child-step" })] }));
      return missing;
    });
    const onOpenRun = vi.fn();

    render(<RunDetailView defaultView="activity" runId="parent-1" onOpenRun={onOpenRun} />);
    openWorkflowNodes();

    // The sub-workflow step id is a link → opens the child run full-page.
    fireEvent.click(screen.getByTitle("Open the sub-workflow run"));
    expect(onOpenRun).toHaveBeenCalledWith("child-1");

    // Collapsed by default — the child run-detail is NOT fetched yet (so N steps cost no polling)…
    expect(useWorkflowRunMock).not.toHaveBeenCalledWith("child-1");

    // …expanding mounts the live child run-detail inline, with its own step trace.
    fireEvent.click(screen.getByText("Sub-workflow run"));
    expect(useWorkflowRunViewMetadataMock).toHaveBeenCalledWith("child-1", true);
    expect(useWorkflowRunMock).not.toHaveBeenCalledWith("child-1");
    fireEvent.click(screen.getAllByText("Workflow nodes")[1]);
    expect(useWorkflowRunMock).toHaveBeenCalledWith("child-1");
    expect(screen.getByText("child-step")).toBeTruthy();
  });

  it("renders the step id as plain text when no navigation handler is given", () => {
    useWorkflowRunMock.mockImplementation((runId: string) =>
      runId === "parent-1" ? ok(detail({ nodes: [node({ nodeId: "sub", childRunId: "child-1" })] })) : missing);

    render(<RunDetailView defaultView="activity" runId="parent-1" />);
    openWorkflowNodes();

    expect(screen.queryByTitle("Open the sub-workflow run")).toBeNull();
    expect(screen.getByText("sub")).toBeTruthy(); // still shown — just not a link
  });

  it("shows no child embed for a non-subworkflow node", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({ nodes: [node({ nodeId: "start" })] })));

    render(<RunDetailView defaultView="activity" runId="parent-1" onOpenRun={vi.fn()} />);
    openWorkflowNodes();

    expect(screen.queryByText("Sub-workflow run")).toBeNull();
    expect(screen.queryByTitle("Open the sub-workflow run")).toBeNull();
  });

  it("does not double-embed the child the run is suspended on (the suspended panel already shows it)", () => {
    useWorkflowRunViewMetadataMock.mockImplementation((runId: string) => ({ isLoading: false, error: null, data: viewMetadata({ runId, status: runId === "parent-1" ? "Suspended" : "Success" }) }));
    useWorkflowRunPendingWaitMock.mockImplementation((runId: string) => ({ isLoading: false, error: null, data: { runId, wait: runId === "parent-1" ? { id: "wait-1", nodeId: "sub", kind: "Subworkflow", token: "child-1", wakeAt: null, promptState: "Missing", promptPrefix: null } : null } }));
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
    fireEvent.click(screen.getAllByText("Workflow nodes").at(-1)!);
    // …so the trace row must NOT offer the same child again (no duplicate embed / double-poll).
    expect(screen.queryByText("Sub-workflow run")).toBeNull();
  });
});

describe("RunDetailView — live agent-code node badge", () => {
  const parkedTitle = "Workflow node is parked (Suspended) while its agent runs";

  it("badges a Suspended agent.run node with its agent run's LIVE status, not 'Suspended'", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      status: "Suspended",
      nodes: [node({ nodeId: "code", status: "Suspended", agentRunId: "ar-1" })],
    })));
    useAgentRunMock.mockImplementation((id?: string) => ({ data: id === "ar-1" ? { status: "Running" } : undefined }));

    render(<RunDetailView defaultView="activity" runId="parent-1" />);
    openWorkflowNodes();

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
    openWorkflowNodes();

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
    openWorkflowNodes();

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
    openWorkflowNodes();
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
    openWorkflowNodes();
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
    openWorkflowNodes();

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
    openWorkflowNodes();
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
    openWorkflowNodes();
    expect(container.querySelector(".wf-run-node-branch")).toBeNull();
    expect(container.querySelector(".wf-map-rollups")).toBeNull();
  });

  it("renders a non-map run exactly as before — no branch badges, no rollup", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({
      nodes: [node({ nodeId: "a" }), node({ nodeId: "b" })],   // empty iteration keys (default)
    })));

    const { container } = render(<RunDetailView defaultView="activity" runId="parent-1" />);
    openWorkflowNodes();
    expect(container.querySelector(".wf-run-node-branch")).toBeNull();
    expect(container.querySelector(".wf-map-rollups")).toBeNull();
  });
});

describe("RunDetailView — run-view tabs", () => {
  beforeEach(() => useWorkflowRunMock.mockImplementation(() => ok(detail({ nodes: [node({ nodeId: "a" })] }))));

  it("keeps Activity on bounded metadata until raw workflow nodes are explicitly expanded", () => {
    render(<RunDetailView defaultView="activity" runId="parent-1" />);

    expect(useWorkflowRunViewMetadataMock).toHaveBeenLastCalledWith("parent-1", true);
    expect(useWorkflowRunMock).not.toHaveBeenCalled();
    expect(useWorkflowRunDetailMock).not.toHaveBeenCalled();

    fireEvent.click(screen.getByText("Workflow nodes"));
    expect(useWorkflowRunDetailMock).toHaveBeenLastCalledWith("parent-1", true);
  });

  it("offers the five run views, Activity first", () => {
    render(<RunDetailView runId="parent-1" />);
    for (const t of ["Activity", "Canvas", "Changes", "Governed tools", "Trace"]) {
      expect(screen.getByRole("tab", { name: t })).toBeInTheDocument();
    }
    expect(screen.getByRole("tab", { name: "Activity" })).toHaveAttribute("aria-selected", "true");
  });

  it("mounts the independent governed-tools consumer only in its non-nested tab and passes active status", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({ status: "Running" })));
    useWorkflowRunIdentityMock.mockReturnValue({ isLoading: false, error: null, data: { id: "parent-1", runNumber: 1, status: "Running" } });
    render(<RunDetailView runId="parent-1" />);

    expect(screen.queryByTestId("governed-tools-panel")).toBeNull();
    expect(governedToolsPanelMock).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("tab", { name: "Governed tools" }));
    expect(screen.getByTestId("governed-tools-panel")).toHaveAttribute("data-run-id", "parent-1");
    expect(screen.getByTestId("governed-tools-panel")).toHaveAttribute("data-active", "true");
  });

  it.each(["canvas", "changes", "governed-tools", "trace"] as const)("does not enable the legacy full-detail reader for standalone %s", (activeView) => {
    render(<RunDetailView runId="parent-1" defaultView={activeView} />);

    expect(useWorkflowRunMock).not.toHaveBeenCalled();
    expect(useWorkflowRunViewMetadataMock).toHaveBeenCalledTimes(activeView === "canvas" ? 1 : 0);
    expect(useWorkflowRunIdentityMock).toHaveBeenCalledTimes(activeView === "canvas" ? 0 : 1);
    expect(useNodeManifestsMock).toHaveBeenCalledTimes(activeView === "canvas" ? 1 : 0);
    expect(useRunPhasesMock).not.toHaveBeenCalled();
  });

  it("keeps Activity and recursively nested details on bounded metadata until a raw fold opens", () => {
    const activity = render(<RunDetailView runId="parent-1" defaultView="activity" />);
    expect(useWorkflowRunMock).not.toHaveBeenCalled();
    expect(useWorkflowRunIdentityMock).not.toHaveBeenCalled();
    expect(useWorkflowRunViewMetadataMock).toHaveBeenLastCalledWith("parent-1", true);
    activity.unmount();

    useWorkflowRunViewMetadataMock.mockClear();
    render(<RunDetailView runId="parent-1" nested />);
    expect(useWorkflowRunMock).not.toHaveBeenCalled();
    expect(useWorkflowRunIdentityMock).not.toHaveBeenCalled();
    expect(useWorkflowRunViewMetadataMock).toHaveBeenLastCalledWith("parent-1", true);
  });

  it("switches polling ownership between Activity, Canvas and Trace without leaving the old reader enabled", () => {
    render(<RunDetailView runId="parent-1" />);
    expect(useWorkflowRunMock).not.toHaveBeenCalled();
    expect(useWorkflowRunViewMetadataMock).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole("tab", { name: "Canvas" }));
    expect(useWorkflowRunMock).not.toHaveBeenCalled();
    expect(useWorkflowRunViewMetadataMock).toHaveBeenLastCalledWith("parent-1", true);
    expect(useWorkflowRunViewMetadataMock).toHaveBeenCalledTimes(2);
    expect(useWorkflowRunIdentityMock).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("tab", { name: "Trace" }));
    expect(useWorkflowRunMock).not.toHaveBeenCalled();
    expect(useWorkflowRunViewMetadataMock).toHaveBeenCalledTimes(2);
    expect(useWorkflowRunIdentityMock).toHaveBeenLastCalledWith("parent-1", true);
  });

  it("keeps the tab escape hatch and reports a missing bounded Canvas run instead of treating it as empty", () => {
    useWorkflowRunViewMetadataMock.mockReturnValue({ isLoading: false, error: null, data: null });
    render(<RunDetailView runId="parent-1" defaultView="canvas" />);

    expect(screen.getByRole("tab", { name: "Activity" })).toBeInTheDocument();
    expect(screen.getByText("Run not found")).toBeInTheDocument();
    expect(screen.queryByText("No nodes executed yet")).toBeNull();
  });

  it("surfaces corrupt bounded topology as typed unavailable metadata, never an exact empty graph", () => {
    useWorkflowRunViewMetadataMock.mockReturnValue({ isLoading: false, error: null,
      data: viewMetadata({ topologyAvailability: "Corrupt", topology: null }) });
    render(<RunDetailView runId="parent-1" defaultView="canvas" />);

    expect(screen.getByText("Execution graph is malformed and was not treated as empty.")).toBeInTheDocument();
    expect(screen.queryByText("No nodes executed yet")).toBeNull();
  });

  it("preserves bounded Canvas status, rerun admission and child-open coordinates", () => {
    const onOpenRun = vi.fn();
    useWorkflowRunViewMetadataMock.mockReturnValue({ isLoading: false, error: null, data: viewMetadata({
      status: "Running",
      topology: { nodes: [{ id: "sub", typeKey: "flow.subworkflow", label: "Child", parentId: null, position: null, width: null, height: null }], edges: [] },
      cells: [{ sourceRunId: "source-1", nodeId: "sub", iterationKey: "", containerKind: null, status: "Suspended",
        startedAt: null, completedAt: null, childRunId: "child-1", agentRunId: null, rerunnableFromHere: true }],
    }) });
    render(<RunDetailView runId="parent-1" defaultView="canvas" onOpenRun={onOpenRun} />);

    expect(runCanvasMock).toHaveBeenCalledWith(expect.objectContaining({ runStatus: "Running", runNodes: [expect.objectContaining({
      nodeId: "sub", inputs: null, outputs: null, childRunId: "child-1", rerunnableFromHere: true,
    })] }));
    fireEvent.click(screen.getByTestId("bounded-run-canvas"));
    expect(onOpenRun).toHaveBeenCalledExactlyOnceWith("child-1");
  });

  it("keeps suspended pending-wait authority in Activity while bounded tabs show an explicit handoff", () => {
    useWorkflowRunIdentityMock.mockReturnValue({ isLoading: false, error: null,
      data: { id: "parent-1", runNumber: 1, status: "Suspended" } });
    render(<RunDetailView runId="parent-1" defaultView="trace" />);

    expect(screen.getByText("Run suspended")).toBeInTheDocument();
    expect(screen.getByText("Open Activity to inspect the authoritative suspension state and any pending action.")).toBeInTheDocument();
    expect(useWorkflowRunMock).not.toHaveBeenCalled();
  });

  it("defaults to the bounded Activity narrative with raw nodes available as a disclosure", () => {
    render(<RunDetailView runId="parent-1" />);
    expect(screen.getByText("Workflow nodes")).toBeInTheDocument();
    expect(screen.queryByText("Node execution")).not.toBeInTheDocument();
    expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();
  });

  it("shows the Changes placeholder, and the Trace tab renders the raw event ledger", () => {
    render(<RunDetailView runId="parent-1" />);

    fireEvent.click(screen.getByRole("tab", { name: "Changes" }));
    expect(screen.getByText("Coming soon")).toBeInTheDocument();
    expect(screen.queryByText("Node execution")).not.toBeInTheDocument();   // narrative is hidden behind the tab

    fireEvent.click(screen.getByRole("tab", { name: "Trace" }));
    expect(screen.queryByText("Coming soon")).not.toBeInTheDocument();      // Trace is no longer a placeholder
    expect(screen.getByText(/no records yet/i)).toBeInTheDocument();        // the raw ledger view (empty in this mock)
  });

  it("hides the tab bar when embedded (nested), so the editor dialog's child runs stay plain", () => {
    render(<RunDetailView runId="parent-1" nested />);
    expect(screen.queryByRole("tab", { name: "Activity" })).not.toBeInTheDocument();
    expect(screen.queryByTestId("governed-tools-panel")).toBeNull();
    expect(governedToolsPanelMock).not.toHaveBeenCalled();
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

    expect(screen.getByText("backend-fix")).toBeInTheDocument();        // the single agent's inline terminal (its title)
    expect(screen.getByText("Workflow nodes")).toBeInTheDocument();     // the node trace is now a fold
    expect(screen.queryByText("Node execution")).not.toBeInTheDocument(); // …lazy, so unmounted while collapsed
  });

  it("keeps structural-workflow raw nodes lazy as well", () => {
    useWorkflowRunMock.mockImplementation(() => ok(detail({ nodes: [node({ nodeId: "start" })] })));
    useRunPhasesMock.mockReturnValue({ data: { phases: [] } });

    render(<RunDetailView runId="parent-1" />);

    expect(screen.getByText("Workflow nodes")).toBeInTheDocument();
    expect(screen.queryByText("Node execution")).not.toBeInTheDocument();
    openWorkflowNodes();
    expect(screen.getByText("Node execution")).toBeInTheDocument();
  });

  it("folds the raw detail WHILE phases are still loading, so an agent run never expands the node trace then collapses it", () => {
    // The entry flicker: before phases resolve, "no agents (yet)" would render the node trace EXPANDED — then it
    // collapses into a fold the instant agents arrive. While genuinely loading we fold from the start (stable layout);
    // only once loaded does a real agent-less run get the primary trace (the test above).
    useWorkflowRunMock.mockImplementation(() => ok(detail({ nodes: [node({ nodeId: "code", agentRunId: "ar1" })] })));
    useRunPhasesMock.mockReturnValue({ data: undefined, isLoading: true });

    render(<RunDetailView runId="parent-1" />);

    expect(screen.getByText("Workflow nodes")).toBeInTheDocument();        // folded (collapsed disclosure), not expanded
    expect(screen.queryByText("Node execution")).not.toBeInTheDocument();  // …so the trace can't show-then-collapse
  });
});
