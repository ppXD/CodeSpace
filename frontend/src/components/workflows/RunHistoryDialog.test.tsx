import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { RunHistoryDialog } from "./RunHistoryDialog";

/**
 * RunHistoryDialog is the in-editor "Activity" surface — the agent-first rename of the old
 * "Run history" dialog. These pin the rename + the generic behaviour so neither regresses:
 *   1. the section label is "Activity" (title) / "No activity yet" (empty) — the old "Run history"
 *      and "No runs yet" strings are gone. "Run" the button and "a run" as a noun deliberately stay;
 *      only the section/nav label became "Activity".
 *   2. the three render states (loading / empty / populated) each show the right shell;
 *   3. clicking a row calls onPick with THAT run's full id (not the truncated display id);
 *   4. the mask click and the Escape key both call onClose.
 *
 * useWorkflowRuns is mocked so the dialog renders without a QueryClient (mirrors RunDetailView.test).
 * useWorkflowRun / useResumeRun are stubbed only because RunStatusBadge is imported from RunDetailView,
 * whose module pulls them from the same hooks file.
 */
const { useWorkflowRunsMock } = vi.hoisted(() => ({ useWorkflowRunsMock: vi.fn() }));

vi.mock("@/hooks/use-workflows", () => ({
  useWorkflowRuns: (workflowId: string) => useWorkflowRunsMock(workflowId),
  useWorkflowRun: vi.fn(),
  useResumeRun: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
}));

const loading = { isLoading: true, error: null, data: undefined };
const empty = { isLoading: false, error: null, data: [] };
const withRows = {
  isLoading: false,
  error: null,
  data: [
    { id: "run-aaaa1111-2222", status: "Success", sourceType: "manual", startedAt: "2026-01-01T00:00:00Z", workflowVersion: 3 },
    { id: "run-bbbb3333-4444", status: "Failed", sourceType: "provider.github.pull_request", startedAt: null, workflowVersion: 2 },
  ],
};

const props = (over: Partial<Parameters<typeof RunHistoryDialog>[0]> = {}) => ({
  workflowId: "wf-1",
  onPick: vi.fn(),
  onClose: vi.fn(),
  ...over,
});

beforeEach(() => useWorkflowRunsMock.mockReset());

describe("RunHistoryDialog — Activity surface (agent-first rename)", () => {
  it("titles the dialog 'Activity' — the old 'Run history' label is gone", () => {
    useWorkflowRunsMock.mockReturnValue(empty);
    render(<RunHistoryDialog {...props()} />);

    expect(screen.getByText("Activity")).toBeTruthy();
    expect(screen.queryByText("Run history")).toBeNull();
  });

  it("shows the loading shell while runs load", () => {
    useWorkflowRunsMock.mockReturnValue(loading);
    render(<RunHistoryDialog {...props()} />);

    expect(screen.getByText("Loading…")).toBeTruthy();
    expect(screen.queryByText("No activity yet")).toBeNull();
  });

  it("shows 'No activity yet' for empty history — the old 'No runs yet' label is gone", () => {
    useWorkflowRunsMock.mockReturnValue(empty);
    render(<RunHistoryDialog {...props()} />);

    expect(screen.getByText("No activity yet")).toBeTruthy();
    expect(screen.queryByText("No runs yet")).toBeNull();
  });

  it("lists each run and calls onPick with the FULL run id when a row is clicked", () => {
    useWorkflowRunsMock.mockReturnValue(withRows);
    const onPick = vi.fn();
    render(<RunHistoryDialog {...props({ onPick })} />);

    // Each row shows the truncated id (id.slice(0,8)); both rows render.
    expect(screen.getByText("run-aaaa")).toBeTruthy();
    expect(screen.getByText("run-bbbb")).toBeTruthy();

    // Clicking a row opens that run by its FULL id, not the truncated display id.
    fireEvent.click(screen.getByText("run-aaaa"));
    expect(onPick).toHaveBeenCalledTimes(1);
    expect(onPick).toHaveBeenCalledWith("run-aaaa1111-2222");
  });

  it("passes the workflowId through to useWorkflowRuns", () => {
    useWorkflowRunsMock.mockReturnValue(empty);
    render(<RunHistoryDialog {...props({ workflowId: "wf-xyz" })} />);

    expect(useWorkflowRunsMock).toHaveBeenCalledWith("wf-xyz");
  });

  it("closes on mask click and on Escape", () => {
    useWorkflowRunsMock.mockReturnValue(empty);
    const onClose = vi.fn();
    const { container } = render(<RunHistoryDialog {...props({ onClose })} />);

    fireEvent.click(container.querySelector(".mdl-mask")!);
    expect(onClose).toHaveBeenCalledTimes(1);

    fireEvent.keyDown(window, { key: "Escape" });
    expect(onClose).toHaveBeenCalledTimes(2);

    // A non-Escape key does nothing.
    fireEvent.keyDown(window, { key: "Enter" });
    expect(onClose).toHaveBeenCalledTimes(2);
  });
});
