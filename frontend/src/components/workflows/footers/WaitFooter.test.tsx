import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";
import type { NodeLiveSignals } from "@/lib/runLiveFold";

import { RunActionsContext } from "../runActionsContext";
import type { WorkflowNodeData } from "../WorkflowNode";
import type { NodeFooterProps } from "./index";
import { WaitFooter, waitResolution } from "./WaitFooter";

/**
 * WaitFooter is the suspend/wait family's footer. These pin: (1) `waitResolution` reads the settled decision
 * defensively off the row outputs; (2) a Suspended node renders a CALM bar — a countdown ring when the wait
 * has a deadline, a parked elapsed otherwise, and NEVER a spinner; (3) it degrades to the rows when no live
 * signal is present; (4) a resolved node stamps the decision; (5) inline approval reuses the run's resume hook.
 */

const { NOW, SINCE, DEADLINE, live, resumeMutate, resumeState } = vi.hoisted(() => ({
  NOW: Date.parse("2026-07-08T00:00:30.000Z"),        // 30s after SINCE, halfway to DEADLINE
  SINCE: Date.parse("2026-07-08T00:00:00.000Z"),
  DEADLINE: Date.parse("2026-07-08T00:01:00.000Z"),   // 60s window
  live: { value: null as NodeLiveSignals | null },
  resumeMutate: vi.fn(),
  resumeState: { isPending: false, isError: false },
}));

// The live suspend signal (A4), the shared clock (A2/A3), and the resume mutation (the SAME hook SuspendedPanel
// drives) are the three seams the footer reads — mock each so the component renders without a live run / SSE /
// QueryClient. `formatElapsed`/`formatCountdown` stay REAL (only `useNowTick` is pinned to a fixed clock).
vi.mock("@/hooks/use-run-live", () => ({ useNodeLiveContext: () => live.value }));
vi.mock("@/hooks/use-now-tick", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/hooks/use-now-tick")>();
  return { ...actual, useNowTick: () => NOW };
});
vi.mock("@/hooks/use-workflows", () => ({
  useResumeRun: () => ({ mutate: resumeMutate, isPending: resumeState.isPending, isError: resumeState.isError }),
}));

beforeEach(() => {
  live.value = null;
  resumeMutate.mockClear();
  resumeState.isPending = false;
  resumeState.isError = false;
});

function row(over: Partial<WorkflowRunNodeSummary> = {}): WorkflowRunNodeSummary {
  return { nodeId: "w", iterationKey: "", status: "Suspended", inputs: {}, outputs: {}, error: null, startedAt: null, completedAt: null, ...over };
}

function nodeData(over: Partial<WorkflowNodeData> = {}): WorkflowNodeData {
  return { nodeId: "w", typeKey: "flow.wait_approval", displayName: "Wait", iconKey: null, kind: "Regular", category: "Logic", label: null, ...over };
}

function signals(wait: NodeLiveSignals["wait"]): NodeLiveSignals {
  return { wait, lastEventSeq: 1 };
}

function renderFooter(props: Partial<NodeFooterProps> & { status: NodeStatus }, runId?: string) {
  const full: NodeFooterProps = { data: nodeData(), status: props.status, rows: props.rows ?? [row({ status: props.status })], title: props.title };
  const el = <WaitFooter {...full} data={props.data ?? full.data} />;
  return runId
    ? render(<RunActionsContext.Provider value={{ runId, isTerminal: false }}>{el}</RunActionsContext.Provider>)
    : render(el);
}

describe("waitResolution", () => {
  it("stamps an approved approval with who approved it", () => {
    expect(waitResolution("flow.wait_approval", [row({ outputs: { approved: true, approvedBy: "alice" } })]))
      .toEqual({ label: "✓ Approved · alice", tone: "ok" });
  });

  it("stamps a rejected approval", () => {
    expect(waitResolution("flow.wait_approval", [row({ outputs: { approved: false } })]))
      .toEqual({ label: "✗ Rejected", tone: "reject" });
  });

  it("stamps a decision's chosen option", () => {
    const res = waitResolution("flow.decision", [row({ outputs: { selectedOption: "ship" } })]);
    expect(res?.tone).toBe("ok");
    expect(res?.label).toContain("ship");
  });

  it("returns null for a kind that carries no decision (a sleep)", () => {
    expect(waitResolution("flow.sleep", [row({ outputs: { foo: 1 } })])).toBeNull();
  });

  it("returns null when the outputs carry no recognisable resolution", () => {
    expect(waitResolution("flow.wait_approval", [row({ outputs: {} })])).toBeNull();
    expect(waitResolution("flow.wait_approval", [row({ outputs: null })])).toBeNull();
  });
});

describe("WaitFooter — Suspended", () => {
  it("renders a depleting countdown ring (no spinner) when the wait has a deadline", () => {
    live.value = signals({ kind: "Approval", sinceMs: SINCE, deadlineAtMs: DEADLINE, payload: { prompt: "Deploy to prod?" } });

    const { container } = renderFooter({ status: "Suspended" });

    const ring = container.querySelector(".wf-ring");
    expect(ring).not.toBeNull();
    expect(ring?.getAttribute("style")).toContain("--wf-ring-p");
    expect(ring?.getAttribute("style")).toContain("50%");                 // 30s remaining of a 60s window
    expect(screen.getByText("0:30")).toBeTruthy();                        // countdown
    expect(container.querySelector(".wf-rf-status-spin")).toBeNull();     // calm — nothing is computing
  });

  it("renders a parked elapsed when the wait has no deadline", () => {
    live.value = signals({ kind: "Timer", sinceMs: SINCE, payload: {} });

    const { container } = renderFooter({ status: "Suspended", data: nodeData({ typeKey: "flow.sleep" }) });

    expect(container.querySelector(".wf-ring")).toBeNull();
    expect(screen.getByText(/Parked/).textContent).toContain("0:30");
    expect(container.querySelector(".wf-rf-status-spin")).toBeNull();
  });

  it("renders from the node when there is no live signal, without throwing", () => {
    live.value = null;

    const { container } = renderFooter({ status: "Suspended", data: nodeData({ typeKey: "flow.decision" }) });

    expect(container.querySelector(".wf-rf-status-spin")).toBeNull();
    expect(screen.getByText("Awaiting decision")).toBeTruthy();                    // decision kind label from typeKey
  });
});

describe("WaitFooter — resolved", () => {
  it("stamps the resolution on a settled node", () => {
    const { container } = renderFooter({
      status: "Success",
      rows: [row({ status: "Success", outputs: { approved: true, approvedBy: "bob" }, startedAt: "2026-07-08T00:00:00Z", completedAt: "2026-07-08T00:00:05Z" })],
    });

    const stamp = container.querySelector(".wf-wait-stamp");
    expect(stamp?.getAttribute("data-tone")).toBe("ok");
    expect(stamp?.textContent).toContain("Approved");
    expect(stamp?.textContent).toContain("bob");
  });
});

describe("WaitFooter — inline approval", () => {
  it("approves via the reused resume hook on click", () => {
    live.value = signals({ kind: "Approval", sinceMs: SINCE, payload: {} });

    renderFooter({ status: "Suspended" }, "run-1");

    fireEvent.click(screen.getByText("Approve"));

    expect(resumeMutate).toHaveBeenCalledWith({ approved: true, comment: undefined }, expect.anything());
  });

  it("rejects via the reused resume hook on click", () => {
    live.value = signals({ kind: "Approval", sinceMs: SINCE, payload: {} });

    renderFooter({ status: "Suspended" }, "run-1");

    fireEvent.click(screen.getByText("Reject"));

    expect(resumeMutate).toHaveBeenCalledWith({ approved: false, comment: undefined }, expect.anything());
  });
});
