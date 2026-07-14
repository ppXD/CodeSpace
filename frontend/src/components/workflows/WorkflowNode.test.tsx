import { fireEvent, render } from "@testing-library/react";
import { ReactFlowProvider, type NodeProps } from "@xyflow/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { WorkflowRunNodeSummary } from "@/api/workflows";

import { RunOpenContext } from "./runOpenContext";
import { WorkflowNode, type WorkflowNodeData } from "./WorkflowNode";

// Stub the agent embeds — this suite tests the node's wiring (that it mounts the timeline for an agent
// row), not the timeline internals (which fetch via their own hooks and are covered separately).
vi.mock("./AgentRunTimeline", () => ({ AgentRunTimeline: ({ agentRunId }: { agentRunId: string }) => <div data-testid="agent-timeline" data-run={agentRunId} /> }));
vi.mock("./AgentToolCalls", () => ({ AgentToolCalls: ({ agentRunId }: { agentRunId: string }) => <div data-testid="agent-toolcalls" data-run={agentRunId} /> }));

// WorkflowNode reads a parked agent.run node's LIVE status via useAgentRun — mock it (the real hook needs a
// QueryClient). `agentHook.status` drives what the node sees; reset after each test.
const agentHook = vi.hoisted(() => ({ status: undefined as string | undefined }));
vi.mock("@/hooks/use-agents", () => ({ useAgentRun: () => ({ data: agentHook.status ? { status: agentHook.status } : undefined }) }));
afterEach(() => { agentHook.status = undefined; });

/**
 * The canvas card leads with the node's HUMAN name (its editable label, falling back to the node
 * type's display name) and shows the immutable node id as a muted sub-line. The id is the reference
 * key used in {{nodes.<id>.outputs.…}} — visible to copy, but no longer the dominant header — so
 * setting a Label visibly "renames" the node.
 */
function renderNode(overrides: Partial<WorkflowNodeData>, onOpenRun?: (runId: string) => void) {
  const data: WorkflowNodeData = {
    nodeId: "review_llm",
    typeKey: "llm.complete",
    displayName: "LLM Complete",
    iconKey: "sparkles",
    kind: "Regular",
    category: "AI",
    label: null,
    ...overrides,
  };
  const props = { id: data.nodeId, data, selected: false } as unknown as NodeProps;
  return render(
    <ReactFlowProvider>
      <RunOpenContext.Provider value={onOpenRun ?? null}>
        <WorkflowNode {...props} />
      </RunOpenContext.Provider>
    </ReactFlowProvider>,
  );
}

describe("WorkflowNode card", () => {
  it("leads with the human label and shows the immutable id as a muted ref", () => {
    const { container } = renderNode({ nodeId: "review_llm", label: "Code review" });

    expect(container.querySelector(".wf-rf-node-title")?.textContent).toBe("Code review");
    expect(container.querySelector(".wf-rf-node-ref")?.textContent).toBe("review_llm");
  });

  it("falls back to the type's display name when the node has no label", () => {
    const { container } = renderNode({ nodeId: "complete", label: null, displayName: "LLM Complete" });

    expect(container.querySelector(".wf-rf-node-title")?.textContent).toBe("LLM Complete");
    expect(container.querySelector(".wf-rf-node-ref")?.textContent).toBe("complete");
  });

  it("carries no run overlay in the editor (no runStatus), and a status badge in a run view", () => {
    const editor = renderNode({});
    expect(editor.container.querySelector(".wf-rf-node")?.getAttribute("data-run-status")).toBeNull();
    expect(editor.container.querySelector(".wf-rf-status-badge")).toBeNull();

    const run = renderNode({ runStatus: "Success" });
    expect(run.container.querySelector(".wf-rf-node")?.getAttribute("data-run-status")).toBe("Success");
    expect(run.container.querySelector('.wf-rf-status-badge[data-status="success"]')).not.toBeNull();
  });

  it("renders spotlight chips when present, and no .wf-rf-spot row when absent (card-height parity)", () => {
    const withChips = renderNode({
      spotlight: [
        { key: "harness", value: "claude-code", tone: "neutral" },
        { key: "schedule", label: "cron", value: "0 9 * * 1-5", tone: "mono" },
      ],
    });
    const chips = withChips.container.querySelectorAll(".wf-rf-spot-chip");
    expect(chips.length).toBe(2);
    expect(chips[0].getAttribute("data-tone")).toBe("neutral");
    expect(chips[0].textContent).toBe("claude-code");
    expect(chips[1].querySelector("b")?.textContent).toBe("cron");   // label prefix
    expect(chips[1].getAttribute("data-tone")).toBe("mono");

    // A node with no spotlight (undefined) renders no extra row at all — same anatomy as today.
    expect(renderNode({}).container.querySelector(".wf-rf-spot")).toBeNull();
    // An explicitly empty array is also nothing extra.
    expect(renderNode({ spotlight: [] }).container.querySelector(".wf-rf-spot")).toBeNull();
  });

  it("marks an unset spotlight chip with data-unset for the muted placeholder", () => {
    const { container } = renderNode({ spotlight: [{ key: "schedule", value: "Cron schedule", unset: true, tone: "neutral" }] });
    expect(container.querySelector(".wf-rf-spot-chip")?.getAttribute("data-unset")).toBe("true");
  });

  it("renders one labelled source handle per named output (logic.if true/false), not an anonymous handle", () => {
    const named = renderNode({ outputs: [{ name: "true", displayName: "True" }, { name: "false", displayName: "False" }] });

    expect(named.container.querySelectorAll(".wf-rf-handle-named").length).toBe(2);
    expect(named.container.querySelector(".wf-rf-h-true .wf-rf-handle-label")?.textContent).toBe("True");
    expect(named.container.querySelector(".wf-rf-h-false .wf-rf-handle-label")?.textContent).toBe("False");

    // a node with no named outputs shows none (its single default handle stays)
    expect(renderNode({}).container.querySelectorAll(".wf-rf-handle-named").length).toBe(0);
  });
});

/** A bare run row with sensible defaults; override per test. */
function row(overrides: Partial<WorkflowRunNodeSummary>): WorkflowRunNodeSummary {
  return {
    nodeId: "review_llm", iterationKey: "", status: "Success",
    inputs: {}, outputs: {}, error: null, startedAt: null, completedAt: null,
    ...overrides,
  };
}

describe("WorkflowNode coze-style result footer", () => {
  it("shows no footer without run rows, and a status·duration bar with rows", () => {
    const editor = renderNode({});
    expect(editor.container.querySelector(".wf-rf-result")).toBeNull();

    const run = renderNode({
      runStatus: "Success",
      runRows: [row({ outputs: { text: "hi" }, startedAt: "2026-06-22T00:00:00.000Z", completedAt: "2026-06-22T00:00:00.632Z" })],
    });
    const bar = run.container.querySelector(".wf-rf-result-bar");
    expect(bar).not.toBeNull();
    expect(run.container.querySelector(".wf-rf-result-label")?.textContent).toBe("Success");
    expect(run.container.querySelector(".wf-rf-result-dur")?.textContent).toBe("0.632s");  // coze-style sub-second precision
  });

  it("expands on click to reveal the node's output", () => {
    const run = renderNode({
      runStatus: "Success",
      runRows: [row({ outputs: { text: "done" }, startedAt: "2026-06-22T00:00:00.000Z", completedAt: "2026-06-22T00:00:00.000Z" })],
    });
    expect(run.container.querySelector(".wf-rf-result-panel")).toBeNull();

    fireEvent.click(run.container.querySelector(".wf-rf-result-bar")!);
    const panel = run.container.querySelector(".wf-rf-result-panel");
    expect(panel).not.toBeNull();
    expect(panel?.textContent).toContain("done");
  });

  it("shows no duration while a node is still running", () => {
    const run = renderNode({
      runStatus: "Running",
      runRows: [row({ status: "Running", outputs: {}, startedAt: "2026-06-22T00:00:00.000Z", completedAt: null })],
    });
    expect(run.container.querySelector(".wf-rf-result-bar")).not.toBeNull();
    expect(run.container.querySelector(".wf-rf-result-dur")).toBeNull();
  });

  it("rolls a loop / non-map fan-out into one bar with a NEUTRAL run count (not the misleading 'branches')", () => {
    // No containerKind → not a flow.map (a loop / try body shares the `<id>#<i>` key shape), so this keeps the
    // coze-style bar; a real flow.map fan-out renders the MapFanout panel instead (see the S3 describe below).
    // The noun is "runs" (neutral) — only a real flow.map says "branches", only a supervisor says "turns".
    const run = renderNode({
      runStatus: "Success",
      runRows: [
        row({ iterationKey: "m#0", outputs: { v: 1 }, startedAt: "2026-06-22T00:00:00.000Z", completedAt: "2026-06-22T00:00:00.000Z" }),
        row({ iterationKey: "m#1", outputs: { v: 2 }, startedAt: "2026-06-22T00:00:00.000Z", completedAt: "2026-06-22T00:00:02.000Z" }),
      ],
    });
    expect(run.container.querySelector(".wf-rf-result-label")?.textContent).toBe("Success · 2 runs");
  });
});

describe("WorkflowNode result footer — agent.run + sub-workflow embeds (S3)", () => {
  it("makes an agent.run node expandable and embeds its live timeline + tool-call audit", () => {
    const run = renderNode({
      runStatus: "Suspended",   // an agent node parks (Suspended) while the agent works
      runRows: [row({ status: "Suspended", outputs: {}, agentRunId: "agent-7", startedAt: "2026-06-22T00:00:00.000Z", completedAt: null })],
    });
    const bar = run.container.querySelector(".wf-rf-result-bar");
    expect(bar?.getAttribute("data-expandable")).toBe("true");   // expandable on agentRunId alone, no output yet

    fireEvent.click(bar!);
    const panel = run.container.querySelector(".wf-rf-result-panel");
    expect(panel?.getAttribute("data-rich")).toBe("true");        // widened for the timeline
    expect(run.getByTestId("agent-timeline").getAttribute("data-run")).toBe("agent-7");
    expect(run.getByTestId("agent-toolcalls").getAttribute("data-run")).toBe("agent-7");
  });

  it("offers to open a sub-workflow node's child run via RunOpenContext", () => {
    const onOpenRun = vi.fn();
    const run = renderNode({
      runStatus: "Success",
      runRows: [row({ outputs: {}, childRunId: "child-run-9", startedAt: "2026-06-22T00:00:00.000Z", completedAt: "2026-06-22T00:00:01.000Z" })],
    }, onOpenRun);

    fireEvent.click(run.container.querySelector(".wf-rf-result-bar")!);
    const open = run.container.querySelector(".wf-rf-result-open");
    expect(open).not.toBeNull();

    fireEvent.click(open!);
    expect(onOpenRun).toHaveBeenCalledWith("child-run-9");
  });

  it("renders a map fan-out as the activity-terminal panel, branches ordered by element index", () => {
    // The backend returns run rows in StartedAt order; here element 2 started before element 0 (parallel map).
    // A flow.map fan-out renders the MapFanout panel (not the plain result-bar list), ordering its branch dots
    // by the iterationKey element index, NOT the array position.
    const run = renderNode({
      runStatus: "Success",
      runRows: [
        row({ containerKind: "flow.map", iterationKey: "items#2", outputs: { v: "c" }, startedAt: "2026-06-22T00:00:00.000Z", completedAt: "2026-06-22T00:00:00.500Z" }),
        row({ containerKind: "flow.map", iterationKey: "items#0", outputs: { v: "a" }, startedAt: "2026-06-22T00:00:01.000Z", completedAt: "2026-06-22T00:00:01.500Z" }),
      ],
    });

    expect(run.container.querySelector(".wf-rf-fanout")).not.toBeNull();   // the fan-out panel, not the plain bar
    expect(run.container.querySelector(".wf-rf-result-bar")).toBeNull();

    const titles = Array.from(run.container.querySelectorAll(".wf-rf-fanout-dot")).map((e) => e.getAttribute("title"));
    expect(titles).toEqual(["#0 · Success", "#2 · Success"]);   // sorted by element index, not array order

    fireEvent.click(run.container.querySelectorAll<HTMLElement>(".wf-rf-fanout-dot")[1]!);   // focus #2
    expect(run.container.querySelector(".wf-rf-fanout-term-ix")?.textContent).toBe("#2");
  });

  it("surfaces a parked agent.run node's live status as Running (not the idle Suspended)", () => {
    agentHook.status = "Running";   // the agent is actively working while the node parks
    const run = renderNode({
      runStatus: "Suspended",
      runRows: [row({ status: "Suspended", agentRunId: "agent-7", startedAt: "2026-06-22T00:00:00.000Z", completedAt: null })],
    });
    expect(run.container.querySelector(".wf-rf-node")?.getAttribute("data-run-status")).toBe("Running");
    expect(run.container.querySelector(".wf-rf-result-label")?.textContent).toBe("Running");
    expect(run.container.querySelector(".wf-rf-result-bar")?.getAttribute("title")).toContain("Parked");   // engine truth on hover
  });

  it("keeps a node Suspended once its agent is no longer active", () => {
    agentHook.status = "Succeeded";   // agent finished; the node hasn't resumed yet
    const run = renderNode({
      runStatus: "Suspended",
      runRows: [row({ status: "Suspended", agentRunId: "agent-7", startedAt: "2026-06-22T00:00:00.000Z", completedAt: null })],
    });
    expect(run.container.querySelector(".wf-rf-node")?.getAttribute("data-run-status")).toBe("Suspended");
  });
});
