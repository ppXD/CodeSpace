import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { WorkflowRunDetail } from "@/api/workflows";
import { RoomRunPane } from "./RoomRunPane";

/**
 * The run companion pane's shell. It mounts the run's RunCanvas (assembled exactly as RunDetailView does) and
 * frames it with a per-turn title + the run's status pill + a close control. These pin the D1 contract: the
 * header reads "畫布 · 回合 {N}" with the live status, the canvas gets the run's pinned definition/nodes/status,
 * and close fires the callback. RunCanvas is ReactFlow-heavy, so it's stubbed to echo the props it receives.
 */
const { useWorkflowRunMock } = vi.hoisted(() => ({ useWorkflowRunMock: vi.fn() }));

// RunTrace (the 紀錄 tab) reads the raw ledger via useRunRecords — mock it so the real trace surface renders without a fetch.
vi.mock("@/hooks/use-workflows", () => ({
  useWorkflowRun: (runId: string) => useWorkflowRunMock(runId),
  useNodeManifests: () => ({ data: [] }),
  useRunRecords: () => ({
    isLoading: false,
    data: { records: [{ sequence: 1, recordType: "run.started", nodeId: null, occurredAt: "2026-07-13T00:00:00Z", payloadJson: "{}" }] },
  }),
}));

vi.mock("@/components/workflows/RunCanvas", () => ({
  RunCanvas: ({ runId, runStatus, focusNodeId }: { runId?: string; runStatus?: string; focusNodeId?: string }) => (
    <div data-testid="run-canvas" data-run-id={runId} data-run-status={runStatus} data-focus-node-id={focusNodeId} />
  ),
}));

function detail(over: Partial<WorkflowRunDetail>): WorkflowRunDetail {
  return {
    id: "run-1", runNumber: 7, workflowId: "w", workflowVersion: 1, sourceType: "manual",
    normalizedPayload: {}, status: "Running", error: null, startedAt: null, completedAt: null,
    createdDate: "2026-07-13T00:00:00Z", nodes: [], outputs: {}, pendingWait: null,
    definition: { schemaVersion: 1, nodes: [], edges: [] },
    ...over,
  };
}

const ok = (data: WorkflowRunDetail) => ({ isLoading: false, error: null, data });

beforeEach(() => { useWorkflowRunMock.mockReset(); });

describe("RoomRunPane", () => {
  it("renders the turn title + status pill and mounts the canvas with the run's props", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({ id: "run-1", status: "Running" })));

    render(<RoomRunPane runId="run-1" turn={3} onClose={vi.fn()} />);

    expect(screen.getByText("畫布 · 回合 3")).toBeInTheDocument();
    expect(screen.getByText("Running")).toBeInTheDocument();  // the run's status pill

    const canvas = screen.getByTestId("run-canvas");
    expect(canvas).toHaveAttribute("data-run-id", "run-1");
    expect(canvas).toHaveAttribute("data-run-status", "Running");
  });

  it("threads the D3 focusNodeId to the canvas (the ?node= deep-link a journal jump set)", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({ id: "run-1" })));

    render(<RoomRunPane runId="run-1" turn={2} focusNodeId="map-1" onClose={vi.fn()} />);

    expect(screen.getByTestId("run-canvas")).toHaveAttribute("data-focus-node-id", "map-1");
  });

  it("calls onClose from the close button", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));
    const onClose = vi.fn();

    render(<RoomRunPane runId="run-1" turn={1} onClose={onClose} />);

    fireEvent.click(screen.getByLabelText("關閉畫布"));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it("shows a graph-snapshot notice (not the canvas) when the run has no pinned definition", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({ definition: null })));

    render(<RoomRunPane runId="run-1" turn={2} onClose={vi.fn()} />);

    expect(screen.queryByTestId("run-canvas")).toBeNull();
    expect(screen.getByText("這次執行沒有可顯示的流程快照。")).toBeInTheDocument();
  });

  it("shows the back button only as an affordance the narrow-screen overlay reveals (also closes)", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));
    const onClose = vi.fn();

    render(<RoomRunPane runId="run-1" turn={1} onClose={onClose} />);

    fireEvent.click(screen.getByLabelText("返回對話"));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  // ─── D5 mini-tabs (畫布 / 變更 / 紀錄) ───

  it("renders the three mini-tabs with 畫布 selected by default (uncontrolled)", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));

    render(<RoomRunPane runId="run-1" turn={1} onClose={vi.fn()} />);

    const tabs = screen.getAllByRole("tab");
    expect(tabs.map((t) => t.textContent)).toEqual(["畫布", "變更", "紀錄"]);
    expect(screen.getByRole("tab", { name: "畫布" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByTestId("run-canvas")).toBeInTheDocument();
  });

  it("switches to the RunTrace surface when 紀錄 is clicked, and aria-selected tracks it", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));

    const { container } = render(<RoomRunPane runId="run-1" turn={1} onClose={vi.fn()} />);

    fireEvent.click(screen.getByRole("tab", { name: "紀錄" }));

    expect(container.querySelector(".run-trace")).not.toBeNull();       // the real trace ledger rendered
    expect(screen.queryByTestId("run-canvas")).toBeNull();               // the canvas is gone
    expect(screen.getByRole("tab", { name: "紀錄" })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByRole("tab", { name: "畫布" })).toHaveAttribute("aria-selected", "false");
  });

  it("shows the coming-soon placeholder when 變更 is clicked", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));

    render(<RoomRunPane runId="run-1" turn={1} onClose={vi.fn()} />);

    fireEvent.click(screen.getByRole("tab", { name: "變更" }));

    expect(screen.getByText("Coming soon")).toBeInTheDocument();
    expect(screen.getByText("Changes")).toBeInTheDocument();
    expect(screen.queryByTestId("run-canvas")).toBeNull();
    expect(screen.getByRole("tab", { name: "變更" })).toHaveAttribute("aria-selected", "true");
  });

  it("is controlled by `view` and reports clicks via onViewChange (URL-driven mode)", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));
    const onViewChange = vi.fn();

    render(<RoomRunPane runId="run-1" turn={1} view="trace" onViewChange={onViewChange} onClose={vi.fn()} />);

    // `view` wins: the trace surface shows even though the internal default is canvas.
    expect(screen.getByRole("tab", { name: "紀錄" })).toHaveAttribute("aria-selected", "true");
    expect(screen.queryByTestId("run-canvas")).toBeNull();

    // A click reports up (no internal switch) so the URL stays the single source of truth.
    fireEvent.click(screen.getByRole("tab", { name: "畫布" }));
    expect(onViewChange).toHaveBeenCalledWith("canvas");
    expect(screen.getByRole("tab", { name: "紀錄" })).toHaveAttribute("aria-selected", "true");  // unchanged until the prop updates
  });

  // ─── D2 follow/pin toggle + jump-to-latest chip ───

  it("shows the follow toggle label in follow mode and the 跟隨中 hint, and fires onToggleBind", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));
    const onToggleBind = vi.fn();

    render(<RoomRunPane runId="run-1" turn={5} mode="follow" onToggleBind={onToggleBind} onClose={vi.fn()} />);

    expect(screen.getByText("跟隨中")).toBeInTheDocument();
    const toggle = screen.getByRole("button", { name: /跟隨最新回合/ });
    fireEvent.click(toggle);
    expect(onToggleBind).toHaveBeenCalledTimes(1);
  });

  it("shows the pinned toggle label in pinned mode (no 跟隨中 hint), and fires onToggleBind", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));
    const onToggleBind = vi.fn();

    render(<RoomRunPane runId="run-1" turn={2} mode="pinned" onToggleBind={onToggleBind} onClose={vi.fn()} />);

    expect(screen.queryByText("跟隨中")).toBeNull();
    fireEvent.click(screen.getByRole("button", { name: /已釘選/ }));
    expect(onToggleBind).toHaveBeenCalledTimes(1);
  });

  it("hides the follow/pin toggle when no mode is supplied (standalone)", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));

    render(<RoomRunPane runId="run-1" turn={1} onClose={vi.fn()} />);

    expect(screen.queryByRole("button", { name: /跟隨最新回合|已釘選/ })).toBeNull();
    expect(screen.queryByText("跟隨中")).toBeNull();
  });

  it("renders the jump-to-latest chip only when jumpToLatest is set, and fires its handler", () => {
    useWorkflowRunMock.mockReturnValue(ok(detail({})));
    const onJumpToLatest = vi.fn();

    const { rerender } = render(<RoomRunPane runId="run-1" turn={2} mode="pinned" jumpToLatest={7} onJumpToLatest={onJumpToLatest} onClose={vi.fn()} />);

    const chip = screen.getByRole("button", { name: /最新:回合 7 執行中/ });
    fireEvent.click(chip);
    expect(onJumpToLatest).toHaveBeenCalledTimes(1);

    // Cleared → the chip is gone.
    rerender(<RoomRunPane runId="run-1" turn={2} mode="pinned" jumpToLatest={null} onJumpToLatest={onJumpToLatest} onClose={vi.fn()} />);
    expect(screen.queryByRole("button", { name: /執行中/ })).toBeNull();
  });
});
