import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { WorkflowDetail } from "@/api/workflows";
import { ActivityTab, OverviewTab } from "./AgentDetailTabPanels";

/**
 * The tab-content wrappers carry the wiring around the (separately-tested) pure panels: data
 * loading, the manual-run branch (inputs → modal, none → run immediately), and opening the run
 * viewer. Hooks are mocked; the dialogs are stubbed so these isolate the wrapper logic.
 */
const { useWorkflowMock, mutateAsyncMock, useWorkflowRunsMock } = vi.hoisted(() => ({
  useWorkflowMock: vi.fn(),
  mutateAsyncMock: vi.fn(),
  useWorkflowRunsMock: vi.fn(),
}));

vi.mock("@/hooks/use-workflows", () => ({
  useWorkflow: (id: string) => useWorkflowMock(id),
  useRunWorkflowManually: () => ({ mutateAsync: mutateAsyncMock, isPending: false, error: null, reset: vi.fn() }),
  useWorkflowRuns: (id: string) => useWorkflowRunsMock(id),
  useWorkflowRun: vi.fn(),
  useResumeRun: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
}));

vi.mock("./RunWorkflowModal", () => ({ RunWorkflowModal: () => <div>run-modal</div> }));
vi.mock("./RunViewerDialog", () => ({ RunViewerDialog: ({ runId }: { runId: string }) => <div>viewer:{runId}</div> }));

const def = (inputs: unknown[]) => ({ nodes: [], edges: [], inputs } as unknown as WorkflowDetail["definition"]);
const wf = (over: Partial<WorkflowDetail> = {}): WorkflowDetail => ({
  id: "w1", teamId: "t1", name: "PR Security Reviewer", description: "Reviews PRs.",
  enabled: true, latestVersion: 3, definition: def([]), activations: [],
  createdDate: "", lastModifiedDate: "", ...over,
});
const loaded = (over: Partial<WorkflowDetail> = {}) => ({ isLoading: false, error: null, data: wf(over) });

beforeEach(() => {
  useWorkflowMock.mockReset();
  mutateAsyncMock.mockReset();
  useWorkflowRunsMock.mockReset();
});

describe("OverviewTab", () => {
  it("shows a loading shell while the agent loads", () => {
    useWorkflowMock.mockReturnValue({ isLoading: true, error: null, data: undefined });
    render(<OverviewTab workflowId="w1" onEditSource={vi.fn()} />);
    expect(screen.getByText("Loading…")).toBeTruthy();
  });

  it("shows 'Agent not found' when the agent is missing", () => {
    useWorkflowMock.mockReturnValue({ isLoading: false, error: null, data: null });
    render(<OverviewTab workflowId="w1" onEditSource={vi.fn()} />);
    expect(screen.getByText("Agent not found")).toBeTruthy();
  });

  it("renders the overview panel when loaded", () => {
    useWorkflowMock.mockReturnValue(loaded());
    render(<OverviewTab workflowId="w1" onEditSource={vi.fn()} />);
    expect(screen.getByText("PR Security Reviewer")).toBeTruthy();
    expect(screen.getByRole("button", { name: /run now/i })).toBeTruthy();
  });

  it("runs immediately (no input modal) when the agent declares no inputs", () => {
    useWorkflowMock.mockReturnValue(loaded({ definition: def([]) }));
    mutateAsyncMock.mockResolvedValue({ runId: "r1" });
    render(<OverviewTab workflowId="w1" onEditSource={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /run now/i }));
    expect(mutateAsyncMock).toHaveBeenCalledWith({ workflowId: "w1", payload: undefined });
    expect(screen.queryByText("run-modal")).toBeNull();
  });

  it("opens the input modal (no immediate run) when the agent declares inputs", () => {
    useWorkflowMock.mockReturnValue(loaded({ definition: def([{ name: "x", type: "string" }]) }));
    render(<OverviewTab workflowId="w1" onEditSource={vi.fn()} />);
    fireEvent.click(screen.getByRole("button", { name: /run now/i }));
    expect(screen.getByText("run-modal")).toBeTruthy();
    expect(mutateAsyncMock).not.toHaveBeenCalled();
  });

  it("wires Edit in Source", () => {
    useWorkflowMock.mockReturnValue(loaded());
    const onEditSource = vi.fn();
    render(<OverviewTab workflowId="w1" onEditSource={onEditSource} />);
    fireEvent.click(screen.getByRole("button", { name: /edit in source/i }));
    expect(onEditSource).toHaveBeenCalledTimes(1);
  });
});

describe("ActivityTab", () => {
  it("renders the run list empty state", () => {
    useWorkflowRunsMock.mockReturnValue({ isLoading: false, error: null, data: [] });
    render(<ActivityTab workflowId="w1" />);
    expect(screen.getByText("No activity yet")).toBeTruthy();
  });

  it("opens the run viewer inline when a row is clicked", () => {
    useWorkflowRunsMock.mockReturnValue({ isLoading: false, error: null, data: [
      { id: "run-aaaa1111", status: "Success", sourceType: "manual", startedAt: null, workflowVersion: 1 },
    ] });
    render(<ActivityTab workflowId="w1" />);
    fireEvent.click(screen.getByText("run-aaaa"));
    expect(screen.getByText("viewer:run-aaaa1111")).toBeTruthy();
  });
});
