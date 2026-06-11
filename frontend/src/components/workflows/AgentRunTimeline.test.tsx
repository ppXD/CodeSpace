import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { AgentRunTimeline } from "./AgentRunTimeline";

const state = vi.hoisted(() => ({
  run: { id: "r1", status: "Running", harness: "claude-code", error: null as string | null, startedAt: "2026-06-11T11:14:03Z", heartbeatAt: new Date().toISOString() as string | null, completedAt: null as string | null, createdDate: "2026-06-11T11:14:03Z" },
  events: [] as { sequence: number; kind: string; text: string; data: string | null; occurredAt: string }[],
}));

vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => ({ data: state.run }),
  useAgentRunEvents: () => ({ data: state.events }),
}));

describe("AgentRunTimeline", () => {
  it("shows the live status + heartbeat while running, and streams events", () => {
    state.run = { ...state.run, status: "Running" };
    state.events = [
      { sequence: 1, kind: "CommandExecuted", text: "npm test", data: null, occurredAt: "2026-06-11T11:15:00Z" },
      { sequence: 2, kind: "AssistantMessage", text: "Analyzing the repo…", data: null, occurredAt: "2026-06-11T11:15:02Z" },
    ];

    render(<AgentRunTimeline agentRunId="r1" />);

    expect(screen.getByText("Running")).toBeInTheDocument();
    expect(screen.getByText(/live ·/)).toBeInTheDocument();           // heartbeat freshness shown while active
    expect(screen.getByText("npm test")).toBeInTheDocument();
    expect(screen.getByText("Analyzing the repo…")).toBeInTheDocument();
    expect(screen.getByText("ran")).toBeInTheDocument();              // CommandExecuted → "ran"
  });

  it("surfaces a failed run's error and no live badge once terminal", () => {
    state.run = { ...state.run, status: "Failed", error: "API Error: 401 Authentication Error", heartbeatAt: null };
    state.events = [];

    render(<AgentRunTimeline agentRunId="r1" />);

    expect(screen.getByText("Failed")).toBeInTheDocument();
    expect(screen.getByText(/401 Authentication Error/)).toBeInTheDocument();
    expect(screen.queryByText(/live ·/)).not.toBeInTheDocument();
    expect(screen.getByText("No activity recorded.")).toBeInTheDocument();
  });
});
