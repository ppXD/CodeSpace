import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

// Stub the densities so this test isolates the wave's header (summary + expand) + the fleet dots + the one-terminal-
// per-wave behavior. The fleet/table/tile stubs render a button per agent (calling onOpen, reflecting open/selected).
vi.mock("./AgentFleetDots", () => ({
  AgentFleetDots: ({ agents, selectedAgentRunId, openId, onOpen }: { agents: PhaseAgentRef[]; selectedAgentRunId?: string | null; openId: string | null; onOpen: (id: string) => void }) =>
    <div data-testid="fleet">
      {agents.map((a) => (
        <button key={a.agentRunId} data-testid="fleet-dot" data-selected={a.agentRunId === selectedAgentRunId || undefined} data-open={a.agentRunId === openId || undefined} onClick={() => onOpen(a.agentRunId)}>{a.agentRunId}</button>
      ))}
    </div>,
}));
vi.mock("./AgentWaveTable", () => ({
  AgentWaveTable: ({ agents, onOpen }: { agents: PhaseAgentRef[]; onOpen: (id: string) => void }) =>
    <div data-testid="wave-table">{agents.map((a) => <button key={a.agentRunId} data-testid="table-row" onClick={() => onOpen(a.agentRunId)}>{a.agentRunId}</button>)}</div>,
}));
vi.mock("./AgentTile", () => ({
  AgentTile: ({ agent, onOpen }: { agent: PhaseAgentRef; onOpen?: () => void }) => <button data-testid="agent-tile" onClick={onOpen}>{agent.agentRunId}</button>,
}));
vi.mock("./AgentTerminal", () => ({
  AgentTerminal: ({ agent, onClose }: { agent: PhaseAgentRef; onClose: () => void }) => <div data-testid="agent-terminal" onClick={onClose}>terminal:{agent.agentRunId}</div>,
}));

import { AgentWave } from "./AgentWave";
import type { AgentWave as AgentWaveModel } from "./runActivity";

const a = (id: string, status = "Running"): PhaseAgentRef => ({ agentRunId: id, status });
const wave = (o: Partial<AgentWaveModel>): AgentWaveModel => ({ id: "w", label: "Implement", startedAt: null, agents: [], ...o });
const expand = () => screen.getByRole("button", { name: /agent detail/i });

describe("AgentWave", () => {
  it("defaults to the fleet card — dots + a breakdown summary, body collapsed", () => {
    render(<AgentWave wave={wave({ agents: [a("a1", "Succeeded"), a("a2", "Running"), a("a3", "Succeeded")] })} />);

    expect(screen.getByText("Implement")).toBeInTheDocument();
    expect(screen.getByText("3 agents · 2 done · 1 running")).toBeInTheDocument();
    expect(screen.getAllByTestId("fleet-dot")).toHaveLength(3);
    expect(screen.queryByTestId("wave-table")).toBeNull();   // body hidden until expanded
    expect(screen.queryByTestId("agent-tile")).toBeNull();
  });

  it("expands to the metrics table, and the Table/Tiles toggle switches the view", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} />);

    fireEvent.click(expand());
    expect(screen.getByTestId("wave-table")).toBeInTheDocument();   // table is the default expanded view
    expect(screen.queryByTestId("agent-tile")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Tiles" }));
    expect(screen.getAllByTestId("agent-tile")).toHaveLength(2);
    expect(screen.queryByTestId("wave-table")).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Table" }));
    expect(screen.getByTestId("wave-table")).toBeInTheDocument();
  });

  it("collapses the body on a second chevron click", () => {
    render(<AgentWave wave={wave({ agents: [a("a1")] })} />);

    fireEvent.click(expand());
    expect(screen.getByTestId("wave-table")).toBeInTheDocument();
    fireEvent.click(expand());
    expect(screen.queryByTestId("wave-table")).toBeNull();
  });

  it("opens an agent's terminal from a fleet dot and collapses it on a re-click", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} />);
    expect(screen.queryByTestId("agent-terminal")).toBeNull();

    fireEvent.click(screen.getByText("a1"));   // the fleet dot
    expect(screen.getByTestId("agent-terminal")).toHaveTextContent("terminal:a1");

    fireEvent.click(screen.getByText("a1"));
    expect(screen.queryByTestId("agent-terminal")).toBeNull();
  });

  it("keeps only ONE terminal open across densities — opening another switches it", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} />);

    fireEvent.click(screen.getByText("a1"));   // open a1 from the fleet dot
    fireEvent.click(expand());                 // expand — the terminal stays open
    fireEvent.click(screen.getAllByTestId("table-row")[1]);   // open a2 from the table — switches

    const terminals = screen.getAllByTestId("agent-terminal");
    expect(terminals).toHaveLength(1);
    expect(terminals[0]).toHaveTextContent("terminal:a2");
  });

  it("marks the selected agent's fleet dot", () => {
    render(<AgentWave wave={wave({ agents: [a("a1"), a("a2")] })} selectedAgentRunId="a2" />);

    const selected = screen.getAllByTestId("fleet-dot").filter((t) => t.dataset.selected === "true");
    expect(selected).toHaveLength(1);
    expect(selected[0]).toHaveTextContent("a2");
  });
});
