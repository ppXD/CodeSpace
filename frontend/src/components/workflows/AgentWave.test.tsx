import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

// Stub the tile as a button (calls onOpen, reflects open) + stub the terminal, so this test isolates the wave's
// header + grid + the one-open-per-wave behavior.
vi.mock("./AgentTile", () => ({
  AgentTile: ({ agent, selected, open, onOpen }: { agent: PhaseAgentRef; selected?: boolean; open?: boolean; onOpen?: () => void }) =>
    <button data-testid="agent-tile" data-selected={selected || undefined} data-open={open || undefined} onClick={onOpen}>{agent.agentRunId}</button>,
}));
vi.mock("./AgentTerminal", () => ({
  AgentTerminal: ({ agent, onClose }: { agent: PhaseAgentRef; onClose: () => void }) =>
    <div data-testid="agent-terminal" onClick={onClose}>terminal:{agent.agentRunId}</div>,
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

  it("opens one terminal on click and collapses it on a re-click", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} />);
    expect(screen.queryByTestId("agent-terminal")).toBeNull();

    fireEvent.click(screen.getByText("a1"));
    expect(screen.getByTestId("agent-terminal")).toHaveTextContent("terminal:a1");

    fireEvent.click(screen.getByText("a1"));   // re-click the open tile collapses it
    expect(screen.queryByTestId("agent-terminal")).toBeNull();
  });

  it("keeps only ONE terminal open — opening another switches it", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} />);

    fireEvent.click(screen.getByText("a1"));
    fireEvent.click(screen.getByText("a2"));

    const terminals = screen.getAllByTestId("agent-terminal");
    expect(terminals).toHaveLength(1);
    expect(terminals[0]).toHaveTextContent("terminal:a2");
  });
});
