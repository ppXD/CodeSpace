import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef, RunPhase } from "@/api/workflows";

const { useRunPhasesMock } = vi.hoisted(() => ({ useRunPhasesMock: vi.fn() }));
vi.mock("@/hooks/use-workflows", () => ({ useRunPhases: () => useRunPhasesMock() }));
vi.mock("./AgentTile", () => ({
  AgentTile: ({ agent, open, onOpen }: { agent: PhaseAgentRef; open?: boolean; onOpen?: () => void }) =>
    <button data-testid="tile" data-open={open || undefined} onClick={onOpen}>{agent.agentRunId}</button>,
}));
vi.mock("./AgentTerminal", () => ({
  AgentTerminal: ({ agent, onClose }: { agent: PhaseAgentRef; onClose: () => void }) => <div data-testid="terminal" onClick={onClose}>terminal:{agent.agentRunId}</div>,
}));

import { RunActivityTiles } from "./RunActivityTiles";

function phase(o: Partial<RunPhase> & { id: string }): RunPhase {
  return { label: o.id, kind: "node", status: "Active", order: 0, agents: [], metrics: { agentCount: 0, succeededCount: 0, failedCount: 0 }, sourceKey: "node-summary", ...o };
}
const a = (id: string): PhaseAgentRef => ({ agentRunId: id, status: "Running" });
const withPhases = (phases: RunPhase[]) => useRunPhasesMock.mockReturnValue({ data: { phases }, isLoading: false });

beforeEach(() => useRunPhasesMock.mockReturnValue({ data: undefined, isLoading: false }));

describe("RunActivityTiles", () => {
  it("shows an empty state once loaded with no agents", () => {
    withPhases([]);
    render(<RunActivityTiles runId="r1" />);
    expect(screen.getByText(/no agents yet/i)).toBeInTheDocument();
  });

  it("tiles every agent in the run when no phase is selected", () => {
    withPhases([phase({ id: "p1", agents: [a("a1")] }), phase({ id: "p2", agents: [a("a2"), a("a3")] })]);
    render(<RunActivityTiles runId="r1" />);
    expect(screen.getAllByTestId("tile")).toHaveLength(3);
  });

  it("shows a phase-specific empty message when the selected phase owns no agents (the run isn't empty)", () => {
    withPhases([phase({ id: "p1", agents: [a("a1")] })]);
    render(<RunActivityTiles runId="r1" selectedPhaseId="other" />);
    expect(screen.getByText(/this phase has no agents/i)).toBeInTheDocument();
  });

  it("filters to the selected phase's agents", () => {
    withPhases([phase({ id: "p1", agents: [a("a1")] }), phase({ id: "p2", agents: [a("a2"), a("a3")] })]);
    render(<RunActivityTiles runId="r1" selectedPhaseId="p2" />);

    const tiles = screen.getAllByTestId("tile");
    expect(tiles.map((t) => t.textContent)).toEqual(["a2", "a3"]);
  });

  it("opens the focused agent's terminal below the grid", () => {
    withPhases([phase({ id: "p1", agents: [a("a1"), a("a2")] })]);
    render(<RunActivityTiles runId="r1" selectedAgentRunId="a2" />);
    expect(screen.getByTestId("terminal")).toHaveTextContent("terminal:a2");
  });

  it("lifts the agent selection when a tile is clicked", () => {
    const onSelectAgent = vi.fn();
    withPhases([phase({ id: "p1", agents: [a("a1")] })]);
    render(<RunActivityTiles runId="r1" onSelectAgent={onSelectAgent} />);

    fireEvent.click(screen.getByTestId("tile"));
    expect(onSelectAgent).toHaveBeenCalledWith("a1");
  });
});
