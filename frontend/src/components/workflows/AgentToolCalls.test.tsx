import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ToolCallView } from "@/api/agents";
import type { TeamMemberSummary } from "@/api/teams";
import { AgentToolCalls } from "./AgentToolCalls";

const state = vi.hoisted(() => ({
  run: { status: "Succeeded" as string },
  toolCalls: [] as ToolCallView[],
  events: [] as { sequence: number; kind: string; text: string; data: string | null; occurredAt: string }[],
  isLoading: false,
  eventsLoading: false,
  identities: new Map<string, TeamMemberSummary>(),
}));

vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => ({ data: state.run }),
  useToolCalls: () => ({ data: state.toolCalls, isLoading: state.isLoading }),
  useAgentRunEvents: () => ({ data: state.events, isLoading: state.eventsLoading }),
}));

vi.mock("@/hooks/use-team-members", () => ({
  useTeamMemberIdentityMap: () => state.identities,
}));

const call = (over: Partial<ToolCallView>): ToolCallView => ({
  toolKind: "git.open_pr",
  status: "Succeeded",
  createdDate: "2026-06-11T11:15:00Z",
  lastModifiedDate: "2026-06-11T11:15:02Z",
  error: null,
  approvedByUserId: null,
  approvedAt: null,
  ...over,
});

describe("AgentToolCalls", () => {
  it("lists each governed tool call with its tool, status badge, and a chronological timestamp", () => {
    state.run = { status: "Running" };
    state.isLoading = false;
    state.identities = new Map();
    state.toolCalls = [
      call({ toolKind: "git.open_pr", status: "Succeeded" }),
      call({ toolKind: "git.merge_pr", status: "AwaitingApproval" }),
      call({ toolKind: "agent.run_command", status: "Failed", error: "exit code 1" }),
    ];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText("git.open_pr")).toBeInTheDocument();
    expect(screen.getByText("git.merge_pr")).toBeInTheDocument();
    expect(screen.getByText("agent.run_command")).toBeInTheDocument();

    // Status badges, in the warm-theme tone vocabulary (Succeeded=ok, AwaitingApproval=pending, Failed=danger).
    expect(screen.getByText("Succeeded")).toBeInTheDocument();
    expect(screen.getByText("Awaiting approval")).toBeInTheDocument();          // camelCase enum → spaced label
    const failed = screen.getByText("Failed");
    expect(failed.className).toContain("wf-status-err");
    expect(screen.getByText("Succeeded").className).toContain("wf-status-ok");
  });

  it("resolves the approver id to a display name and shows when it was approved", () => {
    state.run = { status: "Succeeded" };
    state.isLoading = false;
    state.identities = new Map([
      ["u-7", { userId: "u-7", name: "Dana Reviewer", email: "d@x.io", avatarUrl: null, isBot: false }],
    ]);
    state.toolCalls = [
      call({ toolKind: "git.merge_pr", status: "Succeeded", approvedByUserId: "u-7", approvedAt: "2026-06-11T11:16:00Z" }),
    ];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText(/approved by Dana Reviewer/)).toBeInTheDocument();
  });

  it("surfaces a tool call's redacted error", () => {
    state.run = { status: "Failed" };
    state.isLoading = false;
    state.identities = new Map();
    state.toolCalls = [call({ status: "Failed", error: "403 Forbidden: insufficient scope" })];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText(/403 Forbidden: insufficient scope/)).toBeInTheDocument();
  });

  it("falls back to the agent's actual tool calls when the governed ledger is empty", () => {
    // A Codex / Claude-Code run uses its own harness tools — the governed ledger is empty, but the event stream
    // carries the real ToolCall events. The tab shows those (name + a compact arg preview) rather than "none".
    state.run = { status: "Succeeded" };
    state.isLoading = false;
    state.eventsLoading = false;
    state.identities = new Map();
    state.toolCalls = [];
    state.events = [
      { sequence: 1, kind: "ToolCall", text: "WebSearch", data: '{"id":"c1","name":"WebSearch","query":"ai coding agents"}', occurredAt: "2026-06-11T11:15:00Z" },
      { sequence: 2, kind: "Reasoning", text: "thinking", data: null, occurredAt: "2026-06-11T11:15:01Z" },
      { sequence: 3, kind: "ToolCall", text: "Read", data: '{"id":"c2","name":"Read","path":"src/app.ts"}', occurredAt: "2026-06-11T11:15:02Z" },
    ];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText("WebSearch")).toBeInTheDocument();
    expect(screen.getByText("Read")).toBeInTheDocument();
    expect(screen.queryByText("thinking")).toBeNull();   // a non-tool event is excluded
    expect(screen.getByText(/"query":"ai coding agents"/)).toBeInTheDocument();   // the arg preview, minus id/name
  });

  it("renders a tool call's name + args, and makes long args a click-to-expand block (no lossy ellipsis)", () => {
    const longPath = "/private/var/folders/z7/qrtkqj255vs6dg3wjfkgcn380000gn/T/codespace/really/long/ai-coding-agents-research-report.md";
    state.run = { status: "Succeeded" };
    state.isLoading = false;
    state.eventsLoading = false;
    state.identities = new Map();
    state.toolCalls = [];
    state.events = [
      { sequence: 1, kind: "ToolCall", text: longPath, data: `{"id":"c1","name":"Read","type":"tool_use","input":{"file_path":"${longPath}","limit":100}}`, occurredAt: "2026-06-11T11:15:00Z" },
    ];

    const { container } = render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText("Read")).toBeInTheDocument();   // the tool NAME (from data.name), not the raw path
    const details = container.querySelector("details.tc-argbox");
    expect(details).not.toBeNull();   // long args → a disclosure, not a hard-cut
    const full = container.querySelector(".tc-args-full");
    expect(full?.textContent).toContain(longPath);   // the FULL value is present, expandable — never truncated away
  });

  it("shows the empty state when there are no tool calls at all", () => {
    state.run = { status: "Succeeded" };
    state.isLoading = false;
    state.eventsLoading = false;
    state.identities = new Map();
    state.toolCalls = [];
    state.events = [];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText("No tool calls for this run")).toBeInTheDocument();
  });

  it("renders nothing while the audit is still loading (the timeline already carries the live state)", () => {
    state.run = { status: "Running" };
    state.isLoading = true;
    state.eventsLoading = false;
    state.identities = new Map();
    state.toolCalls = [];
    state.events = [];

    const { container } = render(<AgentToolCalls agentRunId="r1" />);

    expect(container).toBeEmptyDOMElement();
  });
});
