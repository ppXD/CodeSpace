import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PendingDecision, RunPhase, RunPhasesResponse, WorkflowRunStatus, WorkflowRunSummary } from "@/api/workflows";

import { CockpitBoard } from "./CockpitBoard";

// DecisionCard children answer through this hook — stub it so the board renders without a QueryClient.
vi.mock("@/hooks/use-workflows", () => ({ useAnswerDecision: () => ({ mutate: vi.fn(), isPending: false }) }));

const NOW = new Date(2026, 5, 22, 15, 0, 0).getTime();

function run(id: string, status: WorkflowRunStatus, o: Partial<WorkflowRunSummary> = {}): WorkflowRunSummary {
  return { id, workflowId: "w", workflowVersion: 1, workflowName: null, sourceType: "manual", status, error: null, startedAt: new Date(NOW - 18 * 60_000).toISOString(), completedAt: null, createdDate: new Date(NOW).toISOString(), ...o };
}

function decision(o: Partial<PendingDecision>): PendingDecision {
  return { id: "d", grain: "tool_ledger", rootTraceId: "r", decisionType: "confirm", question: "?", options: [], riskLevel: "low", policy: "human_required", createdAt: new Date(NOW).toISOString(), ...o };
}

function phasesFor(runId: string): RunPhasesResponse {
  const phase: RunPhase = {
    id: "impl", label: "Implement", kind: "node", status: "Active", order: 0,
    agents: [{ agentRunId: "a1", status: "Running" }, { agentRunId: "a2", status: "Running" }, { agentRunId: "a3", status: "Succeeded" }],
    metrics: { agentCount: 3, succeededCount: 1, failedCount: 0 }, sourceKey: "s",
  };
  return { runId, runStatus: "Running", phases: [phase] };
}

const board = (o: Partial<Parameters<typeof CockpitBoard>[0]>) =>
  render(<CockpitBoard runs={[]} decisions={[]} phasesByRun={new Map()} filter={null} nowMs={NOW} onOpen={() => {}} {...o} />);

describe("CockpitBoard", () => {
  it("shows the three zones, the answerable decision, and a live state sentence", () => {
    const { container } = board({
      runs: [run("live1", "Running"), run("done1", "Success")],
      decisions: [decision({ id: "dec1", question: "Choose merge strategy" })],
      phasesByRun: new Map([["live1", phasesFor("live1")]]),
    });

    expect(screen.getByText("Needs attention")).toBeTruthy();
    expect(screen.getByText("Choose merge strategy")).toBeTruthy();   // decision card
    expect(screen.getByText("Live")).toBeTruthy();
    expect(screen.getByText(/Implement · 2 of 3 agents active/)).toBeTruthy();   // live sentence from phases
    expect(screen.getByText("History")).toBeTruthy();
    expect(container.querySelectorAll(".run-row2").length).toBe(1);    // the one Success run
    expect(screen.getByText(/Workflow · Manual · v1 · completed/)).toBeTruthy();   // title-cased source + the result-summary meta
  });

  it("titles a row with the workflow name, falling back to the source label when there is none", () => {
    const { container } = board({ runs: [
      run("named", "Success", { workflowName: "Deploy Pipeline" }),
      run("anon", "Success", { workflowName: null, sourceType: "webhook" }),
    ] });

    const titles = [...container.querySelectorAll(".run-row2-title")].map((n) => n.textContent);
    expect(titles).toContain("Deploy Pipeline");   // authored run shows its workflow name (from run.workflowName)
    expect(titles).toContain("Webhook");           // null name → title-cased source label
  });

  it("dedups a suspended run that already has a queued decision", () => {
    const { container } = board({
      runs: [run("susp-with", "Suspended"), run("susp-without", "Suspended")],
      decisions: [decision({ id: "dx", workflowRunId: "susp-with" })],
    });
    // susp-with is covered by its decision → no SuspendedRow; susp-without shows one.
    expect(container.querySelectorAll(".cockpit-attn-row").length).toBe(1);
  });

  it("opens a run from a Review action and from a recent row", () => {
    const onOpen = vi.fn();
    const { container } = board({ runs: [run("s1", "Suspended"), run("r1", "Success")], onOpen });

    fireEvent.click(screen.getByText("Review →"));
    expect(onOpen).toHaveBeenCalledWith("s1");

    fireEvent.click(container.querySelector(".run-row2")!);
    expect(onOpen).toHaveBeenCalledWith("r1");
  });

  it("filter='failed' shows only failed + suspended runs", () => {
    const { container } = board({ filter: "failed", runs: [run("f", "Failure"), run("s", "Suspended"), run("ok", "Success"), run("live", "Running")] });
    expect(screen.getByText("Failed / stuck")).toBeTruthy();
    expect(container.querySelectorAll(".run-row2").length).toBe(2);   // Failure + Suspended only
  });

  it("falls back to the run status in the Live row before its phases + start load", () => {
    const { container } = board({ runs: [run("live1", "Running", { startedAt: null })], phasesByRun: new Map() });   // no phases, no start
    const meta = container.querySelector(".cockpit-live-meta");
    expect(meta?.textContent).toBe("Running");   // degrades to status, no crash
  });

  it("filter='today' shows only runs created today", () => {
    const yesterday = new Date(NOW - 36 * 3_600_000).toISOString();
    const { container } = board({ filter: "today", runs: [
      run("t1", "Success", { createdDate: new Date(NOW).toISOString() }),
      run("t2", "Success", { createdDate: yesterday }),
    ] });
    expect(screen.getByText("Today")).toBeTruthy();
    expect(container.querySelectorAll(".run-row2").length).toBe(1);   // only t1 (today)
  });

  it("shows the calm empty state when nothing needs attention", () => {
    board({ runs: [run("ok", "Success")], decisions: [] });
    expect(screen.getByText(/Nothing needs you/)).toBeTruthy();
  });
});
