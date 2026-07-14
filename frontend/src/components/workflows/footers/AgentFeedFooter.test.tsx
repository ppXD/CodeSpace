import { render } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import type { AgentRunEventDto, ToolCallView } from "@/api/agents";
import type { NodeStatus, WorkflowRunNodeSummary } from "@/api/workflows";

import type { WorkflowNodeData } from "../WorkflowNode";
import { AgentFeedFooter, countChangedFiles, eventIcon, firstPendingApproval, supervisorTone } from "./AgentFeedFooter";

// The footer's whole source is the three agent-run hooks — mock them so the component runs without a
// QueryClient / network. `isAgentRunActive` stays REAL (imported from @/api/agents), driven by the mocked status.
const agentState = vi.hoisted(() => ({
  status: undefined as string | undefined,
  events: [] as AgentRunEventDto[],
  tools: [] as ToolCallView[],
}));
vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => ({ data: agentState.status ? { status: agentState.status } : undefined }),
  useAgentRunEvents: () => ({ data: agentState.events }),
  useToolCalls: () => ({ data: agentState.tools }),
}));
afterEach(() => { agentState.status = undefined; agentState.events = []; agentState.tools = []; });

/** One live event row. */
function ev(sequence: number, kind: string, text: string): AgentRunEventDto {
  return { sequence, kind, text, data: null, occurredAt: "2026-07-13T00:00:00.000Z" };
}

/** A governed tool call parked awaiting a human decision. */
function pendingTool(toolKind: string): ToolCallView {
  return { toolKind, status: "AwaitingApproval", createdDate: "2026-07-13T00:00:00.000Z", lastModifiedDate: "2026-07-13T00:00:00.000Z", error: null, approvedByUserId: null, approvedAt: null };
}

/** A run row for the node — an agent.run row carries an agentRunId by default; override to drop it. */
function agentRow(overrides: Partial<WorkflowRunNodeSummary> = {}): WorkflowRunNodeSummary {
  return { nodeId: "a1", iterationKey: "", status: "Suspended", inputs: {}, outputs: {}, error: null, startedAt: null, completedAt: null, agentRunId: "run-1", ...overrides };
}

/** Minimal node data — the footer reads only typeKey / displayName / nodeId. */
function nodeData(overrides: Partial<WorkflowNodeData> = {}): WorkflowNodeData {
  return { nodeId: "n1", typeKey: "agent.run", displayName: "Agent Run", iconKey: null, kind: "Regular", category: "Agent", label: null, ...overrides };
}

function renderFooter(status: NodeStatus, rows: WorkflowRunNodeSummary[], data: WorkflowNodeData = nodeData()) {
  return render(<AgentFeedFooter data={data} status={status} rows={rows} title={data.displayName} />);
}

/** The per-row icon keys (data-icon) in DOM order. */
function iconKeys(container: HTMLElement): string[] {
  return Array.from(container.querySelectorAll(".wf-rf-feed-row .wf-rf-feed-ic")).map((e) => e.getAttribute("data-icon") ?? "");
}

describe("AgentFeedFooter — pure helpers", () => {
  it("eventIcon maps each known kind to its glyph key and every other kind to the neutral dot", () => {
    expect(eventIcon("ToolCall").key).toBe("wrench");
    expect(eventIcon("CommandExecuted").key).toBe("terminal");
    expect(eventIcon("FileChanged").key).toBe("file-diff");
    expect(eventIcon("TestOutput").key).toBe("beaker");
    expect(eventIcon("AssistantMessage").key).toBe("chat");
    expect(eventIcon("Reasoning").key).toBe("sparkle");
    expect(eventIcon("PlanUpdate").key).toBe("list-checks");
    // Open set: Queued / Started / Completed and any future/unknown kind degrade to a dot, never crash.
    expect(eventIcon("Queued").key).toBe("dot");
    expect(eventIcon("Completed").key).toBe("dot");
    expect(eventIcon("SomeFutureKind").key).toBe("dot");
  });

  it("countChangedFiles dedupes FileChanged targets and ignores other kinds", () => {
    expect(countChangedFiles([ev(1, "FileChanged", "src/a.ts"), ev(2, "FileChanged", "src/a.ts"), ev(3, "FileChanged", "src/b.ts"), ev(4, "ToolCall", "Read")])).toBe(2);
    expect(countChangedFiles([ev(1, "ToolCall", "x")])).toBe(0);
  });

  it("supervisorTone keeps Stopped OFF the success reading (Completed=success, Stopped=warn, AcceptanceFailed=failure)", () => {
    expect(supervisorTone("Completed")).toBe("success");
    expect(supervisorTone("Stopped")).toBe("warn");
    expect(supervisorTone("AcceptanceFailed")).toBe("failure");
    expect(supervisorTone("Whatever")).toBeUndefined();
  });

  it("firstPendingApproval finds an AwaitingApproval ledger row, else null", () => {
    expect(firstPendingApproval([pendingTool("git.push")])?.toolKind).toBe("git.push");
    expect(firstPendingApproval([{ ...pendingTool("x"), status: "Succeeded" }])).toBeNull();
    expect(firstPendingApproval(undefined)).toBeNull();
  });
});

describe("AgentFeedFooter — working feed", () => {
  it("Running with 5 events renders ONLY the last 3, with the correct per-kind icons + a changed-files count", () => {
    agentState.status = "Running";
    agentState.events = [
      ev(1, "FileChanged", "src/a.ts"),        // before the tail — still counted
      ev(2, "FileChanged", "src/b.ts"),        // before the tail — still counted
      ev(3, "ToolCall", "Read the config"),
      ev(4, "CommandExecuted", "npm test"),
      ev(5, "AssistantMessage", "wrapping up the change now"),
    ];
    const { container } = renderFooter("Running", [agentRow({ status: "Running" })]);

    expect(container.querySelector(".wf-rf-feed-title")?.textContent).toBe("代理工作中");
    expect(container.querySelectorAll(".wf-rf-feed-row")).toHaveLength(3);            // DOM capped at 3 rows
    expect(iconKeys(container)).toEqual(["wrench", "terminal", "chat"]);              // per-kind icons, in order
    expect(container.querySelector(".wf-rf-feed-meta")?.textContent).toContain("2 檔");  // deduped across ALL events
  });

  it("an unknown event kind degrades to the neutral dot icon without crashing", () => {
    agentState.status = "Running";
    agentState.events = [ev(1, "SomeFutureKind", "who knows")];
    const { container } = renderFooter("Running", [agentRow({ status: "Running" })]);

    expect(iconKeys(container)).toEqual(["dot"]);
    expect(container.querySelector(".wf-rf-feed-row")?.getAttribute("data-kind")).toBe("SomeFutureKind");
  });

  it("Suspended WITHOUT a pending approval reads as the working feed (distinct from the amber wait)", () => {
    agentState.status = "Running";   // the agent is actively working while the node parks
    agentState.events = [ev(1, "Reasoning", "thinking")];
    const { container } = renderFooter("Suspended", [agentRow()]);

    expect(container.querySelector(".wf-rf-feed")?.getAttribute("data-approval")).toBeNull();
    expect(container.querySelector(".wf-rf-feed-title")?.textContent).toBe("代理工作中");
  });
});

describe("AgentFeedFooter — awaiting approval", () => {
  it("Suspended WITH a pending governed tool call shows the amber wait + inline approve/deny controls", () => {
    agentState.status = "Running";
    agentState.tools = [pendingTool("git.push")];
    const { container, getByRole } = renderFooter("Suspended", [agentRow()]);

    const feed = container.querySelector(".wf-rf-feed");
    expect(feed?.hasAttribute("data-approval")).toBe(true);               // amber approval state
    expect(feed?.querySelector(".wf-rf-feed-title")?.textContent).toContain("等待批准");
    expect(feed?.querySelector(".wf-rf-feed-title")?.textContent).toContain("git.push");
    // Inline decision affordances render (wiring deferred — see the ApprovalBar TODO).
    expect(getByRole("button", { name: "批准" })).not.toBeNull();
    expect(getByRole("button", { name: "拒絕" })).not.toBeNull();
  });
});

describe("AgentFeedFooter — terminal receipt stamp", () => {
  it("terminal Success stamps the reused receipt bar with the summary + a branch chip + changed-files metric", () => {
    const rows = [agentRow({ status: "Success", outputs: { summary: "Refactored the parser", branch: "feat/parser", changedFiles: ["a", "b", "c"] } })];
    const { container } = renderFooter("Success", rows);

    expect(container.querySelector(".wf-rf-result-bar")).not.toBeNull();               // reuses ReceiptFooter's bar
    expect(container.querySelector(".wf-rf-agent-lead")?.textContent).toContain("Refactored the parser");
    expect(container.querySelector(".wf-rf-agent-branch")?.textContent).toContain("feat/parser");
    expect(container.querySelector(".wf-rf-agent-metrics")?.textContent).toContain("+3 檔");
  });

  it("a node with NO agentRunId degrades to the plain receipt (default status label, no feed) without throwing", () => {
    const rows = [agentRow({ status: "Success", agentRunId: null, outputs: { text: "hi" } })];
    const { container } = renderFooter("Success", rows);

    expect(container.querySelector(".wf-rf-result-bar")).not.toBeNull();
    expect(container.querySelector(".wf-rf-result-label")?.textContent).toBe("Success");
    expect(container.querySelector(".wf-rf-feed")).toBeNull();
    expect(container.querySelector(".wf-rf-agent-stamp")).toBeNull();                   // no agent digest to stamp
  });
});

describe("AgentFeedFooter — supervisor variant", () => {
  it("agent.supervisor terminal with status 'Stopped' tints the stamp WARN (a stopped run is not a green success)", () => {
    const rows = [agentRow({ status: "Success", outputs: { status: "Stopped", summary: "停在第三輪" } })];
    const { container } = renderFooter("Success", rows, nodeData({ typeKey: "agent.supervisor", displayName: "Supervisor" }));

    const stamp = container.querySelector(".wf-rf-agent-stamp");
    expect(stamp?.getAttribute("data-tone")).toBe("warn");
    expect(stamp?.getAttribute("data-tone")).not.toBe("success");
    expect(stamp?.textContent).toContain("停在第三輪");
  });

  it("agent.supervisor working feed carries the current turn counter parsed from the row iteration key", () => {
    agentState.status = "Running";
    agentState.events = [ev(1, "PlanUpdate", "decomposed the task")];
    const rows = [agentRow({ status: "Suspended", iterationKey: "sup#turn3#park" })];
    const { container } = renderFooter("Suspended", rows, nodeData({ typeKey: "agent.supervisor", displayName: "Supervisor" }));

    expect(container.querySelector(".wf-rf-feed-turn")?.textContent).toBe("turn 3");
  });
});
