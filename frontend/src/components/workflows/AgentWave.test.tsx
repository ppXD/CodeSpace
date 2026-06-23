import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

// Stub the tile (it pulls live hooks) so this test isolates the wave's header + grid + selection threading.
vi.mock("./AgentTile", () => ({
  AgentTile: ({ agent, selected }: { agent: PhaseAgentRef; selected?: boolean }) =>
    <div data-testid="agent-tile" data-selected={selected || undefined}>{agent.agentRunId}</div>,
}));

import { AgentWave } from "./AgentWave";
import type { AgentWave as AgentWaveModel } from "./runActivity";

const a = (id: string): PhaseAgentRef => ({ agentRunId: id, status: "Running" });
const wave = (o: Partial<AgentWaveModel>): AgentWaveModel => ({ id: "w", label: "Implement", startedAt: null, agents: [], ...o });

describe("AgentWave", () => {
  it("renders the wave label + agent count + one tile per agent", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2"), a("a3")] })} />);

    expect(screen.getByText("Implement")).toBeInTheDocument();
    expect(screen.getByText("3 agents")).toBeInTheDocument();
    expect(screen.getAllByTestId("agent-tile")).toHaveLength(3);
  });

  it("pluralizes the count for a single agent", () => {
    render(<AgentWave wave={wave({ label: "code", agents: [a("a1")] })} />);

    expect(screen.getByText("1 agent")).toBeInTheDocument();
  });

  it("marks only the selected agent's tile", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} selectedAgentRunId="a2" />);

    const selected = screen.getAllByTestId("agent-tile").filter((t) => t.dataset.selected === "true");
    expect(selected).toHaveLength(1);
    expect(selected[0]).toHaveTextContent("a2");
  });
});
