import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { AgentRunTimeline } from "./AgentRunTimeline";

const state = vi.hoisted(() => ({
  run: { id: "r1", status: "Running", harness: "claude-code", error: null as string | null, startedAt: "2026-06-11T11:14:03Z", heartbeatAt: new Date().toISOString() as string | null, completedAt: null as string | null, createdDate: "2026-06-11T11:14:03Z" },
  events: [] as { sequence: number; kind: string; text: string; data: string | null; occurredAt: string }[],
  hasOlder: false,
  olderEventsOmitted: false,
  newerEventsOmitted: false,
  atLatest: true,
  error: null as Error | null,
  loadOlder: vi.fn(),
  returnToLatest: vi.fn(),
}));

vi.mock("@/hooks/use-agents", () => ({
  useAgentRun: () => ({ data: state.run }),
  useAgentRunEventWindow: () => ({ data: state.events, isLoading: false, isLoadingOlder: false, error: state.error, hasOlder: state.hasOlder, olderEventsOmitted: state.olderEventsOmitted, newerEventsOmitted: state.newerEventsOmitted, atLatest: state.atLatest, loadOlder: state.loadOlder, returnToLatest: state.returnToLatest }),
}));

beforeEach(() => {
  state.run = { ...state.run, status: "Running", error: null, heartbeatAt: new Date().toISOString() };
  state.events = [];
  state.hasOlder = false;
  state.olderEventsOmitted = false;
  state.newerEventsOmitted = false;
  state.atLatest = true;
  state.error = null;
  state.loadOlder.mockReset();
  state.returnToLatest.mockReset();
});

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

  it("labels omitted ranges and exposes bounded Older / Return latest controls", () => {
    state.run = { ...state.run, status: "Running" };
    state.events = [{ sequence: 10, kind: "AssistantMessage", text: "middle", data: null, occurredAt: "2026-06-11T11:15:02Z" }];
    state.hasOlder = true;
    state.olderEventsOmitted = true;
    state.newerEventsOmitted = true;
    state.atLatest = false;
    state.error = new Error("page unavailable");

    render(<AgentRunTimeline agentRunId="r1" />);
    fireEvent.click(screen.getByRole("button", { name: "Load earlier activity" }));
    fireEvent.click(screen.getByRole("button", { name: "Return to latest activity" }));

    expect(screen.getByText("Earlier activity omitted.")).toBeInTheDocument();
    expect(screen.getByText("Newer activity omitted.")).toBeInTheDocument();
    expect(screen.getByText("page unavailable")).toBeInTheDocument();
    expect(state.loadOlder).toHaveBeenCalledOnce();
    expect(state.returnToLatest).toHaveBeenCalledOnce();
  });
});
