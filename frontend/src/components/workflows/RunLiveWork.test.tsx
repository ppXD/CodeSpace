import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef, RunPhase } from "@/api/workflows";

import { dedupRunAgents } from "./runPhases";

// AgentCard pulls live hooks; stub it so this test isolates the lead-strip derivation + agent aggregation.
vi.mock("./AgentCard", () => ({
  AgentCard: ({ agent }: { agent: PhaseAgentRef }) => <div data-testid="agent-card">{agent.agentRunId}</div>,
}));

import { RunLiveWork } from "./RunLiveWork";

function phase(over: Partial<RunPhase>): RunPhase {
  return { id: "p", label: "Phase", kind: "node", status: "Active", order: 0, agents: [], metrics: { agentCount: 0, succeededCount: 0, failedCount: 0 }, sourceKey: "node-summary", ...over };
}
const agent = (id: string): PhaseAgentRef => ({ agentRunId: id, status: "Running" });
const cards = () => screen.queryAllByTestId("agent-card");

describe("RunLiveWork — conditional lead strip", () => {
  it("shows a Supervisor strip when the run has a supervisor ledger", () => {
    render(<RunLiveWork phases={[phase({ sourceKey: "supervisor-ledger", status: "Active", label: "Implement", agents: [agent("a1")] })]} />);
    expect(screen.getByText("Supervisor")).toBeInTheDocument();
    expect(screen.getByText("on Implement")).toBeInTheDocument();
    expect(cards()).toHaveLength(1);
  });

  it("shows a Planner strip for a multi-agent fan-out (no supervisor)", () => {
    render(<RunLiveWork phases={[phase({ agents: [agent("a1"), agent("a2")] })]} />);
    expect(screen.getByText("Planner")).toBeInTheDocument();
    expect(screen.getByText("2 agents in parallel")).toBeInTheDocument();
    expect(cards()).toHaveLength(2);
  });

  it("shows a node-path strip for a structural workflow with no agents", () => {
    render(<RunLiveWork phases={[phase({ label: "PR opened" }), phase({ id: "p2", label: "Fetch diff" })]} />);
    expect(screen.getByText("Workflow")).toBeInTheDocument();
    expect(screen.getByText("PR opened → Fetch diff")).toBeInTheDocument();
    expect(cards()).toHaveLength(0);
  });

  it("shows NO strip for a lone agent — its own card leads", () => {
    const { container } = render(<RunLiveWork phases={[phase({ agents: [agent("a1")] })]} />);
    expect(container.querySelector(".run-leadstrip")).toBeNull();
    expect(cards()).toHaveLength(1);
  });

  it("renders nothing when no phase is agent-shaped (caller falls back to the node list)", () => {
    const { container } = render(<RunLiveWork phases={[phase({ label: "only one node" })]} />);
    expect(container.querySelector(".run-livework")).toBeNull();
  });

  it("dedupes an agent listed across several phases into one card", () => {
    render(<RunLiveWork phases={[
      phase({ id: "spawn", sourceKey: "supervisor-ledger", agents: [agent("a1")] }),
      phase({ id: "semantic", sourceKey: "supervisor-ledger", kind: "phase", agents: [agent("a1")] }),
    ]} />);
    expect(cards()).toHaveLength(1);
  });
});

describe("dedupRunAgents", () => {
  it("collapses the same agentRunId across phases, keeps distinct ones", () => {
    const phases = [phase({ agents: [agent("a1"), agent("a2")] }), phase({ id: "p2", agents: [agent("a1")] })];
    expect(dedupRunAgents(phases).map((a) => a.agentRunId)).toEqual(["a1", "a2"]);
  });
});
