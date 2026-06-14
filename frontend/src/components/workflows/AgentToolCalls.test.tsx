import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { ToolCallView } from "@/api/agents";
import type { TeamMemberSummary } from "@/api/teams";
import { AgentToolCalls } from "./AgentToolCalls";

const state = vi.hoisted(() => ({
  run: { status: "Succeeded" as string },
  toolCalls: [] as ToolCallView[],
  isLoading: false,
  identities: new Map<string, TeamMemberSummary>(),
}));

vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => ({ data: state.run }),
  useToolCalls: () => ({ data: state.toolCalls, isLoading: state.isLoading }),
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

  it("shows the empty state when there are no governed tool calls", () => {
    state.run = { status: "Succeeded" };
    state.isLoading = false;
    state.identities = new Map();
    state.toolCalls = [];

    render(<AgentToolCalls agentRunId="r1" />);

    expect(screen.getByText("No governed tool calls for this run")).toBeInTheDocument();
  });

  it("renders nothing while the audit is still loading (the timeline already carries the live state)", () => {
    state.run = { status: "Running" };
    state.isLoading = true;
    state.identities = new Map();
    state.toolCalls = [];

    const { container } = render(<AgentToolCalls agentRunId="r1" />);

    expect(container).toBeEmptyDOMElement();
  });
});
