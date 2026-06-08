import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { WorkflowDetail, WorkflowRunSummary } from "@/api/workflows";
import { AgentOverviewPanel } from "./AgentOverviewPanel";

/**
 * AgentOverviewPanel is the agent-first "home" — a read-only summary + the two primary actions.
 * Pure/prop-driven, so these cover every branch directly:
 *   1. name + description render; null description → placeholder;
 *   2. no activations → "Manual only"; with activations → one chip per trigger typeKey;
 *   3. enabled→"Enabled"/paused→"Paused", plus the version;
 *   4. Run now → onRun, Edit in Source → onEditSource;
 *   5. running → Run disabled + "Running…".
 */
const wf = (over: Partial<WorkflowDetail> = {}): WorkflowDetail => ({
  id: "w1",
  teamId: "t1",
  name: "PR Security Reviewer",
  description: "Reviews every PR for security issues.",
  enabled: true,
  latestVersion: 3,
  definition: { nodes: [], edges: [] } as unknown as WorkflowDetail["definition"],
  activations: [],
  createdDate: "2026-01-01T00:00:00Z",
  lastModifiedDate: "2026-01-01T00:00:00Z",
  ...over,
});

const run = (over: Partial<WorkflowRunSummary> = {}): WorkflowRunSummary => ({
  id: "run-aaaa1111", workflowId: "w1", workflowVersion: 3, sourceType: "manual",
  status: "Success", error: null, startedAt: "2026-01-01T00:00:00Z", completedAt: null, createdDate: "",
  ...over,
});

describe("AgentOverviewPanel", () => {
  it("shows the agent name + description", () => {
    render(<AgentOverviewPanel workflow={wf()} onRun={vi.fn()} onEditSource={vi.fn()} />);
    expect(screen.getByText("PR Security Reviewer")).toBeTruthy();
    expect(screen.getByText("Reviews every PR for security issues.")).toBeTruthy();
  });

  it("falls back to a placeholder when there is no description", () => {
    render(<AgentOverviewPanel workflow={wf({ description: null })} onRun={vi.fn()} onEditSource={vi.fn()} />);
    expect(screen.getByText("No description yet.")).toBeTruthy();
  });

  it("shows 'Manual only' when there are no triggers", () => {
    render(<AgentOverviewPanel workflow={wf({ activations: [] })} onRun={vi.fn()} onEditSource={vi.fn()} />);
    expect(screen.getByText("Manual only")).toBeTruthy();
  });

  it("renders a chip per trigger typeKey", () => {
    render(<AgentOverviewPanel workflow={wf({ activations: [
      { id: "a1", typeKey: "trigger.pr.opened", enabled: true, config: {} },
      { id: "a2", typeKey: "schedule.cron", enabled: true, config: {} },
    ] })} onRun={vi.fn()} onEditSource={vi.fn()} />);
    expect(screen.getByText("trigger.pr.opened")).toBeTruthy();
    expect(screen.getByText("schedule.cron")).toBeTruthy();
    expect(screen.queryByText("Manual only")).toBeNull();
  });

  it("reflects enabled/paused status + version", () => {
    const { rerender } = render(<AgentOverviewPanel workflow={wf({ enabled: true, latestVersion: 3 })} onRun={vi.fn()} onEditSource={vi.fn()} />);
    expect(screen.getByText("Enabled")).toBeTruthy();
    expect(screen.getByText("v3")).toBeTruthy();
    rerender(<AgentOverviewPanel workflow={wf({ enabled: false, latestVersion: 3 })} onRun={vi.fn()} onEditSource={vi.fn()} />);
    expect(screen.getByText("Paused")).toBeTruthy();
  });

  it("wires the two primary actions", () => {
    const onRun = vi.fn();
    const onEditSource = vi.fn();
    render(<AgentOverviewPanel workflow={wf()} onRun={onRun} onEditSource={onEditSource} />);
    fireEvent.click(screen.getByRole("button", { name: /run now/i }));
    expect(onRun).toHaveBeenCalledTimes(1);
    fireEvent.click(screen.getByRole("button", { name: /edit in source/i }));
    expect(onEditSource).toHaveBeenCalledTimes(1);
  });

  it("disables Run + shows 'Running…' while a run is starting", () => {
    render(<AgentOverviewPanel workflow={wf()} onRun={vi.fn()} onEditSource={vi.fn()} running />);
    const btn = screen.getByRole("button", { name: /running/i });
    expect(btn.hasAttribute("disabled")).toBe(true);
  });

  it("shows 'No runs yet' in Recent activity when there are no recent runs", () => {
    render(<AgentOverviewPanel workflow={wf()} recentRuns={[]} onRun={vi.fn()} onEditSource={vi.fn()} />);
    expect(screen.getByText(/No runs yet/)).toBeTruthy();
  });

  it("lists recent runs and wires View all", () => {
    const onViewActivity = vi.fn();
    render(<AgentOverviewPanel workflow={wf()} recentRuns={[run({ id: "run-zzzz9999" })]} onRun={vi.fn()} onEditSource={vi.fn()} onViewActivity={onViewActivity} />);
    expect(screen.getByText("run-zzzz")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: /view all/i }));
    expect(onViewActivity).toHaveBeenCalledTimes(1);
  });

  it("caps the recent list at 5", () => {
    const many = Array.from({ length: 8 }, (_, i) => run({ id: `run-${i}0000000` }));
    render(<AgentOverviewPanel workflow={wf()} recentRuns={many} onRun={vi.fn()} onEditSource={vi.fn()} onViewActivity={vi.fn()} />);
    expect(screen.getAllByText(/^run-/).length).toBe(5);
  });
});
