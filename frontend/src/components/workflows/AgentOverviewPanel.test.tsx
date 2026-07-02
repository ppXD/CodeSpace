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
  id: "run-aaaa1111", workflowId: "w1", workflowVersion: 3, workflowName: null, sourceType: "manual",
  status: "Success", error: null, startedAt: "2026-01-01T00:00:00Z", completedAt: null, createdDate: "",
  rootRunId: "run-aaaa1111", attemptCount: 1, rootSourceType: "manual", hasSession: true,
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

  it("reflects enabled/paused status (via the power toggle) + a labeled version", () => {
    const { rerender } = render(<AgentOverviewPanel workflow={wf({ enabled: true, latestVersion: 3 })} onRun={vi.fn()} onEditSource={vi.fn()} onToggleEnabled={vi.fn()} />);
    expect(screen.getByText("Enabled")).toBeTruthy();
    expect(screen.getByText("Version")).toBeTruthy();
    expect(screen.getByText("v3")).toBeTruthy();
    rerender(<AgentOverviewPanel workflow={wf({ enabled: false, latestVersion: 3 })} onRun={vi.fn()} onEditSource={vi.fn()} onToggleEnabled={vi.fn()} />);
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

  it("shows 'No runs yet' under the Runs section when there are none", () => {
    render(<AgentOverviewPanel workflow={wf()} runs={[]} onRun={vi.fn()} onEditSource={vi.fn()} />);
    expect(screen.getByText("Runs")).toBeTruthy();
    expect(screen.getByText(/No runs yet/)).toBeTruthy();
  });

  it("lists EVERY run (no cap) and opens a row via onOpenRun", () => {
    const onOpenRun = vi.fn();
    const many = Array.from({ length: 8 }, (_, i) => run({ id: `run-${i}0000000` }));
    render(<AgentOverviewPanel workflow={wf()} runs={many} onRun={vi.fn()} onEditSource={vi.fn()} onOpenRun={onOpenRun} />);
    expect(screen.getAllByText(/^run-/).length).toBe(8);   // full list, not the old cap of 5

    fireEvent.click(screen.getByText("run-3000"));          // r.id.slice(0,8) of "run-30000000"
    expect(onOpenRun).toHaveBeenCalledWith("run-30000000");
  });
});

describe("AgentOverviewPanel header lifecycle controls (moved from Settings)", () => {
  it("shows an 'Enabled' power toggle when enabled and fires onToggleEnabled on click", () => {
    const onToggleEnabled = vi.fn();
    render(<AgentOverviewPanel workflow={wf({ enabled: true })} onRun={vi.fn()} onEditSource={vi.fn()} onToggleEnabled={onToggleEnabled} />);
    const toggle = screen.getByRole("button", { name: /enabled/i });
    expect(toggle.getAttribute("data-enabled")).toBe("true");
    fireEvent.click(toggle);
    expect(onToggleEnabled).toHaveBeenCalledTimes(1);
  });

  it("shows a 'Paused' power toggle when disabled", () => {
    render(<AgentOverviewPanel workflow={wf({ enabled: false })} onRun={vi.fn()} onEditSource={vi.fn()} onToggleEnabled={vi.fn()} />);
    expect(screen.getByRole("button", { name: /paused/i }).getAttribute("data-enabled")).toBe("false");
  });

  it("omits the toggle + delete when their handlers aren't provided", () => {
    render(<AgentOverviewPanel workflow={wf()} onRun={vi.fn()} onEditSource={vi.fn()} />);
    expect(screen.queryByRole("button", { name: /enabled|paused/i })).toBeNull();
    expect(screen.queryByRole("button", { name: /delete workflow/i })).toBeNull();
  });

  it("renders a 'Delete workflow' button that fires onDelete", () => {
    const onDelete = vi.fn();
    render(<AgentOverviewPanel workflow={wf()} onRun={vi.fn()} onEditSource={vi.fn()} onDelete={onDelete} />);
    fireEvent.click(screen.getByRole("button", { name: /delete workflow/i }));
    expect(onDelete).toHaveBeenCalledTimes(1);
  });
});
