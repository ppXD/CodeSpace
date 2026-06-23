import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

// Stub the three densities so this test isolates the wave's header + which density renders + the one-terminal-per-wave
// behavior. The table + tile stubs render a button per agent (calling onOpen, reflecting open/selected).
vi.mock("./AgentWaveTable", () => ({
  AgentWaveTable: ({ agents, selectedAgentRunId, openId, onOpen }: { agents: PhaseAgentRef[]; selectedAgentRunId?: string | null; openId: string | null; onOpen: (id: string) => void }) =>
    <div data-testid="wave-table">
      {agents.map((a) => (
        <button key={a.agentRunId} data-testid="table-row" data-selected={a.agentRunId === selectedAgentRunId || undefined} data-open={a.agentRunId === openId || undefined} onClick={() => onOpen(a.agentRunId)}>{a.agentRunId}</button>
      ))}
    </div>,
}));
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

const a = (id: string, status = "Running"): PhaseAgentRef => ({ agentRunId: id, status });
const wave = (o: Partial<AgentWaveModel>): AgentWaveModel => ({ id: "w", label: "Implement", startedAt: null, agents: [], ...o });
const toggle = () => screen.getByRole("button", { name: /terminal tiles|compact table/i });

describe("AgentWave", () => {
  it("defaults to the compact table — no terminal tiles until expanded", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2"), a("a3")] })} />);

    expect(screen.getByText("Implement")).toBeInTheDocument();
    expect(screen.getByText(/3 agents/)).toBeInTheDocument();
    expect(screen.getByTestId("wave-table")).toBeInTheDocument();
    expect(screen.getAllByTestId("table-row")).toHaveLength(3);
    expect(screen.queryByTestId("agent-tile")).toBeNull();
  });

  it("pluralizes the count for a single agent", () => {
    render(<AgentWave wave={wave({ label: "code", agents: [a("a1")] })} />);
    expect(screen.getByText(/1 agent\b/)).toBeInTheDocument();
  });

  it("shows progress squares — one filled per done agent", () => {
    const { container } = render(<AgentWave wave={wave({ agents: [a("a1", "Succeeded"), a("a2", "Running"), a("a3", "Succeeded")] })} />);

    const squares = container.querySelectorAll(".agent-wave-progress > i");
    expect(squares).toHaveLength(3);
    expect(container.querySelectorAll(".agent-wave-progress > i[data-on]")).toHaveLength(2);
  });

  it("toggles between the compact table and the terminal tiles", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} />);

    fireEvent.click(toggle());   // table → tiles
    expect(screen.getAllByTestId("agent-tile")).toHaveLength(2);
    expect(screen.queryByTestId("wave-table")).toBeNull();

    fireEvent.click(toggle());   // tiles → table
    expect(screen.getByTestId("wave-table")).toBeInTheDocument();
    expect(screen.queryByTestId("agent-tile")).toBeNull();
  });

  it("opens an agent's terminal from a table row and collapses it on a re-click", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} />);
    expect(screen.queryByTestId("agent-terminal")).toBeNull();

    fireEvent.click(screen.getByText("a1"));
    expect(screen.getByTestId("agent-terminal")).toHaveTextContent("terminal:a1");

    fireEvent.click(screen.getByText("a1"));   // re-click the open row collapses it
    expect(screen.queryByTestId("agent-terminal")).toBeNull();
  });

  it("opens a terminal from a tile too, keeping the open agent across a density toggle", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} />);

    fireEvent.click(screen.getByText("a1"));   // open from the table row
    fireEvent.click(toggle());                 // switch to tiles — the terminal stays open
    expect(screen.getByTestId("agent-terminal")).toHaveTextContent("terminal:a1");

    const openTile = screen.getAllByTestId("agent-tile").find((t) => t.dataset.open === "true");
    expect(openTile).toHaveTextContent("a1");
  });

  it("keeps only ONE terminal open — opening another switches it", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} />);

    fireEvent.click(screen.getByText("a1"));
    fireEvent.click(screen.getByText("a2"));

    const terminals = screen.getAllByTestId("agent-terminal");
    expect(terminals).toHaveLength(1);
    expect(terminals[0]).toHaveTextContent("terminal:a2");
  });

  it("marks the selected agent's row", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} selectedAgentRunId="a2" />);

    const selected = screen.getAllByTestId("table-row").filter((t) => t.dataset.selected === "true");
    expect(selected).toHaveLength(1);
    expect(selected[0]).toHaveTextContent("a2");
  });
});
