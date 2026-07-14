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
vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => ({ data: agentHook.status ? { status: agentHook.status } : undefined }),
  useAgentRunEvents: () => ({ data: [] }),
  useToolCalls: () => ({ data: [] }),
}));
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

  it("threads data.hot onto the card as data-hot (C3 animation budget), and omits it otherwise", () => {
    const hot = renderNode({ runStatus: "Running", hot: true });
    expect(hot.container.querySelector(".wf-rf-node")?.getAttribute("data-hot")).toBe("true");

    // A running-but-not-hot node carries no marker → the CSS gives it the calm static ring, not the pulse.
    const cold = renderNode({ runStatus: "Running", hot: false });
    expect(cold.container.querySelector(".wf-rf-node")?.hasAttribute("data-hot")).toBe(false);
    expect(renderNode({ runStatus: "Running" }).container.querySelector(".wf-rf-node")?.hasAttribute("data-hot")).toBe(false);
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
    // A receipt-family node pins the generic coze bar's mid-run behavior (no duration until the row completes).
    // llm.complete now owns the bespoke tokenStream footer, whose Running treatment (Generating + a live estimate /
    // elapsed) is covered in TokenStreamFooter.test.tsx.
    const run = renderNode({
      typeKey: "trigger.manual", category: "Triggers",
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
      typeKey: "agent.run", category: "Agent",
      runStatus: "Suspended",
      runRows: [row({ status: "Suspended", agentRunId: "agent-7", startedAt: "2026-06-22T00:00:00.000Z", completedAt: null })],
    });
    // Card override: the parked-on-a-live-agent node paints as Running, not an idle wait.
    expect(run.container.querySelector(".wf-rf-node")?.getAttribute("data-run-status")).toBe("Running");
    // Footer: agent.run routes to the agent feed, whose live "working" head is the not-idle signal
    // (the feed's own moods — working vs awaiting-approval vs receipt — are covered in AgentFeedFooter.test.tsx).
    expect(run.container.querySelector(".wf-rf-feed-title")?.textContent).toBe("Agent working");
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

/**
 * The B7 one-shot status beats: a node that TRANSITIONS into a beat-worthy Success while mounted stamps a
 * `data-beat` on its card (which the A1 CSS plays once). The initial render must NOT beat — opening a finished
 * run is silent (that no-replay guard is covered exhaustively in useStatusBeat.test.ts).
 */
function renderForBeat(initial: Partial<WorkflowNodeData>) {
  const build = (over: Partial<WorkflowNodeData>) => {
    const data: WorkflowNodeData = {
      nodeId: "n", typeKey: "trigger.push", displayName: "Trigger", iconKey: null, kind: "Trigger", category: "Triggers", label: null,
      ...over,
    };
    const props = { id: data.nodeId, data, selected: false } as unknown as NodeProps;
    return (
      <ReactFlowProvider>
        <RunOpenContext.Provider value={null}>
          <WorkflowNode {...props} />
        </RunOpenContext.Provider>
      </ReactFlowProvider>
    );
  };
  const utils = render(build(initial));
  return { ...utils, rerenderWith: (over: Partial<WorkflowNodeData>) => utils.rerender(build(over)) };
}

describe("WorkflowNode status beats", () => {
  it("stamps data-beat=ignite when a trigger transitions to Success (and none on the initial render)", () => {
    const r = renderForBeat({ kind: "Trigger", typeKey: "trigger.push", runStatus: "Running" });
    expect(r.container.querySelector(".wf-rf-node")?.getAttribute("data-beat")).toBeNull();   // no replay on mount

    r.rerenderWith({ kind: "Trigger", typeKey: "trigger.push", runStatus: "Success" });
    expect(r.container.querySelector(".wf-rf-node")?.getAttribute("data-beat")).toBe("ignite");
  });

  it("stamps data-beat=settle when a terminal transitions to Success", () => {
    const r = renderForBeat({ kind: "Terminal", typeKey: "builtin.terminal", runStatus: "Running" });
    r.rerenderWith({ kind: "Terminal", typeKey: "builtin.terminal", runStatus: "Success" });
    expect(r.container.querySelector(".wf-rf-node")?.getAttribute("data-beat")).toBe("settle");
  });
});

/**
 * C2 — the live counter in a container frame's HEADER (.wf-rf-loop-meta). Distinct from the fan-out FOOTER
 * (BranchDotsFooter / NodeRunFooter) the container already renders — this suite asserts only the header read.
 *
 * Loop-iteration convention: the pass number is the highest body iteration index seen PLUS 1 (1-based), so
 * rows at #0/#1/#2 read "第 3 輪" (the loop has entered its 3rd pass). The "/ max" suffix appears only when the
 * card carries `config.maxIterations` — which definitionToRfNodes does NOT copy onto card data today, so the
 * suffix is normally absent (degraded to a bare count). Try's taken-handle stamp is DEFERRED: the run row has
 * no routingHints field, so the header renders nothing for a try rather than fake a signal.
 */
describe("ContainerNode live header counter (C2)", () => {
  function mapRow(index: number, status: WorkflowRunNodeSummary["status"]): WorkflowRunNodeSummary {
    return row({ nodeId: "worker", containerKind: "flow.map", iterationKey: `enrich#${index}`, status });
  }
  function loopRow(index: number, status: WorkflowRunNodeSummary["status"] = "Success"): WorkflowRunNodeSummary {
    return row({ nodeId: "step", containerKind: "flow.loop", iterationKey: `poll#${index}`, status });
  }

  it("map header reads {done}/{total} branches with the B4 running/waiting breakdown", () => {
    const run = renderNode({
      nodeId: "enrich", typeKey: "flow.map", displayName: "Map", kind: "Map", category: "Logic",
      runStatus: "Running",
      runRows: [
        mapRow(0, "Success"), mapRow(1, "Success"), mapRow(2, "Success"),
        mapRow(3, "Running"), mapRow(4, "Suspended"),   // 1 running + 1 waiting(Suspended)
      ],
    });
    const meta = run.container.querySelector(".wf-rf-loop-meta");
    expect(meta).not.toBeNull();
    expect(meta?.textContent).toContain("3/5");        // done / total
    expect(meta?.textContent).toContain("branches");
    expect(meta?.textContent).toContain("1 running");
    expect(meta?.textContent).toContain("waiting");        // the Suspended branch reads as waiting, per B4
  });

  it("loop header reads the current pass = highest iteration index + 1", () => {
    const run = renderNode({
      nodeId: "poll", typeKey: "flow.loop", displayName: "Loop", kind: "Loop", category: "Logic",
      runStatus: "Running",
      runRows: [loopRow(0), loopRow(1), loopRow(2, "Running")],
    });
    const meta = run.container.querySelector(".wf-rf-loop-meta");
    expect(meta?.textContent).toContain("Round 3");   // indices #0..#2 → 3rd pass
    expect(meta?.textContent).not.toContain("/");  // no maxIterations on the card → no "/ N" suffix
  });

  it("loop header appends / max when the card carries config.maxIterations", () => {
    const run = renderNode({
      nodeId: "poll", typeKey: "flow.loop", displayName: "Loop", kind: "Loop", category: "Logic",
      runStatus: "Running",
      config: { maxIterations: 5 },
      runRows: [loopRow(0), loopRow(1), loopRow(2)],
    });
    expect(run.container.querySelector(".wf-rf-loop-meta")?.textContent).toContain("Round 3 / 5");
  });

  it("try header is deferred — renders the frame but no meta (routingHints not on the run row)", () => {
    const run = renderNode({
      nodeId: "guard", typeKey: "flow.try", displayName: "Try", kind: "Try", category: "Logic",
      runStatus: "Success",
      runRows: [row({ nodeId: "guard", iterationKey: "", status: "Success" })],
    });
    expect(run.container.querySelector(".wf-rf-loop")).not.toBeNull();       // frame still renders
    expect(run.container.querySelector(".wf-rf-loop-meta")).toBeNull();      // no header stamp for a try
  });

  it("shows no meta for a container with no run rows (editor)", () => {
    const editor = renderNode({ nodeId: "poll", typeKey: "flow.loop", displayName: "Loop", kind: "Loop", category: "Logic" });
    expect(editor.container.querySelector(".wf-rf-loop")).not.toBeNull();
    expect(editor.container.querySelector(".wf-rf-loop-meta")).toBeNull();
  });

  it("shows no map meta when the container carries only its own non-branch row (degrades gracefully)", () => {
    // In a run view a container's own runRows are its single top-level row (empty iterationKey), NOT the fanned
    // body branch rows — so fanBranches finds nothing and the header renders no meta. See the report's note.
    const run = renderNode({
      nodeId: "enrich", typeKey: "flow.map", displayName: "Map", kind: "Map", category: "Logic",
      runStatus: "Running",
      runRows: [row({ nodeId: "enrich", iterationKey: "", containerKind: null, status: "Running" })],
    });
    expect(run.container.querySelector(".wf-rf-loop-meta")).toBeNull();
  });
});
