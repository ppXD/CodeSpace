import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

vi.mock("./AgentTerminal", () => ({
  AgentTerminal: ({ agent, onClose }: { agent: PhaseAgentRef; onClose?: () => void }) =>
    <div data-testid="terminal" data-closable={!!onClose} onClick={onClose}>term:{agent.agentRunId}</div>,
}));
vi.mock("./AgentTile", () => ({
  AgentTile: ({ agent, onOpen }: { agent: PhaseAgentRef; onOpen?: () => void }) => <button data-testid="tile" onClick={onOpen}>{agent.agentRunId}</button>,
}));

import { TimelinePhase } from "./TimelinePhase";
import type { AgentWave } from "./runActivity";

const a = (id: string, status = "Running"): PhaseAgentRef => ({ agentRunId: id, status });
const wave = (o: Partial<AgentWave>): AgentWave => ({ id: "w", label: "Implement", startedAt: null, agents: [], ...o });
const dots = (c: HTMLElement) => Array.from(c.querySelectorAll<HTMLElement>(".run-tl-dots > i"));

beforeEach(() => { Element.prototype.scrollIntoView = vi.fn(); });

describe("TimelinePhase", () => {
  it("a single-agent wave renders its full terminal directly — no marker, no tiles, not collapsible", () => {
    render(<TimelinePhase wave={wave({ agents: [a("a1")] })} />);

    expect(screen.getByTestId("terminal")).toHaveTextContent("term:a1");
    expect(screen.queryByTestId("tile")).toBeNull();
    expect(screen.queryByText("Implement")).toBeNull();
    expect(screen.getByTestId("terminal").dataset.closable).toBe("false");
  });

  it("a multi-agent wave shows the marker (label + a status dot per agent + a live summary) over the tiles", () => {
    const { container } = render(<TimelinePhase wave={wave({ agents: [a("a1", "Running"), a("a2", "Succeeded"), a("a3", "Queued")] })} />);

    expect(screen.getByText("Implement")).toBeInTheDocument();
    expect(dots(container).map((d) => d.dataset.state)).toEqual(["running", "done", "waiting"]);
    expect(screen.getByText("1 running · 1 queued")).toBeInTheDocument();
    expect(screen.getAllByTestId("tile")).toHaveLength(3);
    expect(screen.queryByTestId("terminal")).toBeNull();   // no agent focused yet
  });

  it("summarizes a settled phase as 'done' once nothing is running or queued", () => {
    render(<TimelinePhase wave={wave({ agents: [a("a1", "Succeeded"), a("a2", "Succeeded")] })} />);
    expect(screen.getByText("done")).toBeInTheDocument();
  });

  it("lifts the selection on a tile click and opens the focused agent's terminal below", () => {
    const onSelectAgent = vi.fn();
    const { rerender } = render(<TimelinePhase wave={wave({ agents: [a("a1"), a("a2")] })} onSelectAgent={onSelectAgent} />);

    fireEvent.click(screen.getByText("a1"));
    expect(onSelectAgent).toHaveBeenCalledWith("a1");

    rerender(<TimelinePhase wave={wave({ agents: [a("a1"), a("a2")] })} selectedAgentRunId="a2" onSelectAgent={onSelectAgent} />);
    expect(screen.getByTestId("terminal")).toHaveTextContent("term:a2");
    expect(screen.getByTestId("terminal").dataset.closable).toBe("true");   // collapsible when opened from a tile
  });
});
