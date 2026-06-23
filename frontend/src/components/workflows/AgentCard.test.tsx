import { render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

const { useAgentRunMock, useAgentRunEventsMock } = vi.hoisted(() => ({
  useAgentRunMock: vi.fn(),
  useAgentRunEventsMock: vi.fn(),
}));

vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => useAgentRunMock(),
  useAgentRunEvents: () => useAgentRunEventsMock(),
  useToolCalls: () => ({ data: [], isLoading: false }),   // only used by the expand, never in these tests
}));
vi.mock("@/hooks/use-team-members", () => ({ useTeamMemberIdentityMap: () => new Map() }));

import { AgentCard } from "./AgentCard";

const agent: PhaseAgentRef = { agentRunId: "ar1", status: "Succeeded", label: "backend-fix" };
const evt = (kind: string, sequence: number) => ({ sequence, kind, text: kind, occurredAt: "2026-06-22T00:05:00Z", data: null });

beforeEach(() => {
  // A terminal agent: 0s→7m59s, harness claude-code.
  useAgentRunMock.mockReturnValue({ data: { id: "ar1", status: "Succeeded", harness: "claude-code", createdDate: "2026-06-22T00:00:00Z", completedAt: "2026-06-22T00:07:59Z", startedAt: null, heartbeatAt: null, error: null } });
  useAgentRunEventsMock.mockReturnValue({ data: [] });
});

describe("AgentCard rollup", () => {
  it("rolls up file edits + tool uses from the event stream, with the harness + duration", () => {
    useAgentRunEventsMock.mockReturnValue({ data: [evt("FileChanged", 1), evt("FileChanged", 2), evt("ToolCall", 3), evt("AssistantMessage", 4)] });

    render(<AgentCard agent={agent} />);

    expect(screen.getByText("backend-fix")).toBeInTheDocument();
    expect(screen.getByText("claude-code · 2 files · 1 tool · 7m 59s")).toBeInTheDocument();
  });

  it("omits zero counts — meta gracefully degrades to what's known", () => {
    render(<AgentCard agent={agent} />);   // no events
    expect(screen.getByText("claude-code · 7m 59s")).toBeInTheDocument();
  });
});
