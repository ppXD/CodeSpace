import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { WorkflowRunViewMetadata } from "@/api/workflowRunViewMetadataApi";
import { RoomRunPane } from "./RoomRunPane";

/**
 * The run companion pane's shell. It mounts the run's RunCanvas (assembled exactly as RunDetailView does) and
 * frames it with a per-turn title + the run's status pill + a close control. These pin the D1 contract: the
 * header reads "Canvas · Turn {N}" with the live status, the canvas gets the run's pinned definition/nodes/status,
 * and close fires the callback. RunCanvas is ReactFlow-heavy, so it's stubbed to echo the props it receives.
 */
const { legacyRunDetail, useIdentity, useViewMetadata } = vi.hoisted(() => ({ legacyRunDetail: vi.fn(), useIdentity: vi.fn(), useViewMetadata: vi.fn() }));

// RunTrace reads a bounded raw-ledger window — mock it so the real trace surface renders without a fetch.
vi.mock("@/hooks/use-workflows", () => ({
  useWorkflowRun: (runId: string) => legacyRunDetail(runId),
  useWorkflowRunIdentity: (runId: string, enabled: boolean) => useIdentity(runId, enabled),
  useNodeManifests: () => ({ data: [] }),
  useRunRecordWindow: () => ({
    records: [{ sequence: 1, recordType: "run.started", nodeId: null, iterationKey: "", occurredAt: "2026-07-13T00:00:00Z", payloadJson: "{}" }],
    isLoading: false, isLoadingOlder: false, error: null, hasOlder: false, olderRecordsOmitted: false, newerRecordsOmitted: false, atLatest: true,
    loadOlder: vi.fn(), returnToLatest: vi.fn(),
  }),
  useRunDataCompleteness: () => ({
    isLoading: false,
    error: null,
    data: { runId: "run-1", scope: "RecordedFacetsOnly", facets: [], hasStatements: false, isTerminal: false, requiredFacets: [], missingFacetStatements: [], runWideVerdict: null, truncated: false },
  }),
}));

vi.mock("@/hooks/use-workflow-run-view-metadata", () => ({
  useWorkflowRunViewMetadata: (runId: string, enabled: boolean) => useViewMetadata(runId, enabled),
}));

vi.mock("@/components/workflows/RunCanvas", () => ({
  RunCanvas: ({ runId, runStatus, focusNodeId }: { runId?: string; runStatus?: string; focusNodeId?: string }) => (
    <div data-testid="run-canvas" data-run-id={runId} data-run-status={runStatus} data-focus-node-id={focusNodeId} />
  ),
}));

function detail(over: Partial<WorkflowRunViewMetadata>): WorkflowRunViewMetadata {
  return {
    runId: "run-1", runNumber: 7, workflowId: null, workflowVersion: 1, sourceType: "manual", parentRunId: null,
    status: "Running", hasError: false, startedAt: null, completedAt: null, createdDate: "2026-07-13T00:00:00Z",
    scope: "LineageMerged", cellsAvailability: "Available", linksAvailability: "Available", cells: [],
    topologyAvailability: "Available", topology: { nodes: [], edges: [] },
    ...over,
  };
}

const ok = (data: WorkflowRunViewMetadata) => ({ isLoading: false, error: null, data });

beforeEach(() => {
  legacyRunDetail.mockReset();
  useIdentity.mockReset().mockReturnValue({ isLoading: false, error: null, data: { id: "run-1", runNumber: 7, status: "Running" } });
  useViewMetadata.mockReset().mockReturnValue(ok(detail({})));
});

describe("RoomRunPane", () => {
  it("renders the turn title + status pill and mounts the canvas with the run's props", () => {
    useViewMetadata.mockReturnValue(ok(detail({ runId: "run-1", status: "Running" })));

    render(<RoomRunPane runId="run-1" turn={3} onClose={vi.fn()} />);

    expect(screen.getByText("Canvas · Turn 3")).toBeInTheDocument();
    expect(screen.getByText("Running")).toBeInTheDocument();  // the run's status pill

    const canvas = screen.getByTestId("run-canvas");
    expect(canvas).toHaveAttribute("data-run-id", "run-1");
    expect(canvas).toHaveAttribute("data-run-status", "Running");
    expect(useIdentity).toHaveBeenCalledExactlyOnceWith("run-1", false);
    expect(useViewMetadata).toHaveBeenCalledExactlyOnceWith("run-1", true);
  });

  it("threads the D3 focusNodeId to the canvas (the ?node= deep-link a journal jump set)", () => {
    useViewMetadata.mockReturnValue(ok(detail({ runId: "run-1" })));

    render(<RoomRunPane runId="run-1" turn={2} focusNodeId="map-1" onClose={vi.fn()} />);

    expect(screen.getByTestId("run-canvas")).toHaveAttribute("data-focus-node-id", "map-1");
  });

  it("calls onClose from the close button", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));
    const onClose = vi.fn();

    render(<RoomRunPane runId="run-1" turn={1} onClose={onClose} />);

    fireEvent.click(screen.getByLabelText("Close canvas"));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("shows a graph-snapshot notice (not the canvas) when the run has no pinned definition", () => {
    useViewMetadata.mockReturnValue(ok(detail({ topologyAvailability: "Unavailable", topology: null })));

    render(<RoomRunPane runId="run-1" turn={2} onClose={vi.fn()} />);

    expect(screen.queryByTestId("run-canvas")).toBeNull();
    expect(screen.getByText("Execution graph was not recorded.")).toBeInTheDocument();
  });

  it("shows the back button only as an affordance the narrow-screen overlay reveals (also closes)", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));
    const onClose = vi.fn();

    render(<RoomRunPane runId="run-1" turn={1} onClose={onClose} />);

    fireEvent.click(screen.getByLabelText("Back to conversation"));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  // ─── D5 mini-tabs (Canvas / Changes / Trace) ───

  it("renders the three mini-tabs with Canvas selected by default (uncontrolled)", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));

    render(<RoomRunPane runId="run-1" turn={1} onClose={vi.fn()} />);

    const tabs = screen.getAllByRole("tab");
    expect(tabs.map((t) => t.textContent)).toEqual(["Canvas", "Changes", "Trace"]);
    expect(screen.getByRole("tab", { name: "Canvas" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("run-canvas")).toBeInTheDocument();
  });

  it("switches to the RunTrace surface when Trace is clicked, and aria-selected tracks it", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));

    const { container } = render(<RoomRunPane runId="run-1" turn={1} onClose={vi.fn()} />);

    fireEvent.click(screen.getByRole("tab", { name: "Trace" }));

    expect(container.querySelector(".run-trace")).not.toBeNull();       // the real trace ledger rendered
    expect(screen.queryByTestId("run-canvas")).toBeNull();               // the canvas is gone
    expect(screen.getByRole("tab", { name: "Trace" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tab", { name: "Canvas" })).toHaveAttribute("aria-selected", "false");
    expect(useIdentity).toHaveBeenLastCalledWith("run-1", true);
    expect(useViewMetadata).toHaveBeenLastCalledWith("run-1", false);
  });

  it("shows the coming-soon placeholder when Changes is clicked", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));

    render(<RoomRunPane runId="run-1" turn={1} onClose={vi.fn()} />);

    fireEvent.click(screen.getByRole("tab", { name: "Changes" }));

    expect(screen.getByText("Coming soon")).toBeInTheDocument();
    expect(screen.queryByTestId("run-canvas")).toBeNull();
    expect(screen.getByRole("tab", { name: "Changes" })).toHaveAttribute("aria-selected", "true");
  });

  it.each(["changes", "trace"] as const)("does not fetch the legacy full run detail for the %s companion view", (view) => {
    render(<RoomRunPane runId="run-1" turn={1} view={view} onClose={vi.fn()} />);

    expect(legacyRunDetail).not.toHaveBeenCalled();
    expect(useIdentity).toHaveBeenCalledExactlyOnceWith("run-1", true);
    expect(useViewMetadata).toHaveBeenCalledExactlyOnceWith("run-1", false);
  });

  it("is controlled by `view` and reports clicks via onViewChange (URL-driven mode)", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));
    const onViewChange = vi.fn();

    render(<RoomRunPane runId="run-1" turn={1} view="trace" onViewChange={onViewChange} onClose={vi.fn()} />);

    // `view` wins: the trace surface shows even though the internal default is canvas.
    expect(screen.getByRole("tab", { name: "Trace" })).toHaveAttribute("aria-selected", "true");
    expect(screen.queryByTestId("run-canvas")).toBeNull();

    // A click reports up (no internal switch) so the URL stays the single source of truth.
    fireEvent.click(screen.getByRole("tab", { name: "Canvas" }));
    expect(onViewChange).toHaveBeenCalledWith("canvas");
    expect(screen.getByRole("tab", { name: "Trace" })).toHaveAttribute("aria-selected", "true");  // unchanged until the prop updates
  });

  // ─── D2 follow/pin toggle + jump-to-latest chip ───

  it("shows the follow toggle label in follow mode and the Following hint, and fires onToggleBind", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));
    const onToggleBind = vi.fn();

    render(<RoomRunPane runId="run-1" turn={5} mode="follow" onToggleBind={onToggleBind} onClose={vi.fn()} />);

    expect(screen.getByText("Following")).toBeInTheDocument();
    const toggle = screen.getByRole("button", { name: /Follow latest/ });
    fireEvent.click(toggle);
    expect(onToggleBind).toHaveBeenCalledTimes(1);
  });

  it("shows the pinned toggle label in pinned mode (no Following hint), and fires onToggleBind", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));
    const onToggleBind = vi.fn();

    render(<RoomRunPane runId="run-1" turn={2} mode="pinned" onToggleBind={onToggleBind} onClose={vi.fn()} />);

    expect(screen.queryByText("Following")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /Pinned/ }));
    expect(onToggleBind).toHaveBeenCalledTimes(1);
  });

  it("hides the follow/pin toggle when no mode is supplied (standalone)", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));

    render(<RoomRunPane runId="run-1" turn={1} onClose={vi.fn()} />);

    expect(screen.queryByRole("button", { name: /Follow latest|Pinned/ })).toBeNull();
    expect(screen.queryByText("Following")).toBeNull();
  });

  it("renders the jump-to-latest chip only when jumpToLatest is set, and fires its handler", () => {
    useViewMetadata.mockReturnValue(ok(detail({})));
    const onJumpToLatest = vi.fn();

    const { rerender } = render(<RoomRunPane runId="run-1" turn={2} mode="pinned" jumpToLatest={7} onJumpToLatest={onJumpToLatest} onClose={vi.fn()} />);

    const chip = screen.getByRole("button", { name: /Latest: Turn 7 Running/ });
    fireEvent.click(chip);
    expect(onJumpToLatest).toHaveBeenCalledTimes(1);

    // Cleared → the chip is gone.
    rerender(<RoomRunPane runId="run-1" turn={2} mode="pinned" jumpToLatest={null} onJumpToLatest={onJumpToLatest} onClose={vi.fn()} />);
    expect(screen.queryByRole("button", { name: /Running/ })).toBeNull();
  });
});
