import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { AgentActivityPanel } from "./AgentActivityPanel";

/**
 * AgentActivityPanel = the agent's run list, surfaced as a tab. Owns its own useWorkflowRuns
 * fetch (mocked here, like RunHistoryDialog.test). Covers: loading / empty ("No activity yet") /
 * populated, opening a row by its FULL id, and threading the workflowId to the fetch.
 */
const { useWorkflowRunsMock } = vi.hoisted(() => ({ useWorkflowRunsMock: vi.fn() }));

vi.mock("@/hooks/use-workflows", () => ({
  useWorkflowRuns: (id: string) => useWorkflowRunsMock(id),
  useWorkflowRun: vi.fn(),
  useResumeRun: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
}));

const loading = { isLoading: true, error: null, data: undefined };
const empty = { isLoading: false, error: null, data: [] };
const populated = {
  isLoading: false,
  error: null,
  data: [
    { id: "run-aaaa1111-2222", status: "Success", sourceType: "manual", startedAt: "2026-01-01T00:00:00Z", workflowVersion: 3 },
    { id: "run-bbbb3333-4444", status: "Failed", sourceType: "schedule.cron", startedAt: null, workflowVersion: 2 },
  ],
};

beforeEach(() => useWorkflowRunsMock.mockReset());

describe("AgentActivityPanel", () => {
  it("shows the loading shell while runs load", () => {
    useWorkflowRunsMock.mockReturnValue(loading);
    render(<AgentActivityPanel workflowId="w1" onOpenRun={vi.fn()} />);
    expect(screen.getByText("Loading…")).toBeTruthy();
  });

  it("shows 'No activity yet' when there are no runs", () => {
    useWorkflowRunsMock.mockReturnValue(empty);
    render(<AgentActivityPanel workflowId="w1" onOpenRun={vi.fn()} />);
    expect(screen.getByText("No activity yet")).toBeTruthy();
  });

  it("lists each run and opens the FULL id on row click", () => {
    useWorkflowRunsMock.mockReturnValue(populated);
    const onOpenRun = vi.fn();
    render(<AgentActivityPanel workflowId="w1" onOpenRun={onOpenRun} />);

    expect(screen.getByText("run-aaaa")).toBeTruthy();
    expect(screen.getByText("run-bbbb")).toBeTruthy();

    fireEvent.click(screen.getByText("run-aaaa"));
    expect(onOpenRun).toHaveBeenCalledWith("run-aaaa1111-2222");
  });

  it("threads the workflowId through to useWorkflowRuns", () => {
    useWorkflowRunsMock.mockReturnValue(empty);
    render(<AgentActivityPanel workflowId="w-xyz" onOpenRun={vi.fn()} />);
    expect(useWorkflowRunsMock).toHaveBeenCalledWith("w-xyz");
  });
});
