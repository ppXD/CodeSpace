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

const a = (id: string, status = "Running", durationMs?: number): PhaseAgentRef => ({ agentRunId: id, status, durationMs });
const wave = (o: Partial<AgentWave>): AgentWave => ({ id: "w", kind: "phase", label: "Implement", startedAt: null, agents: [], ...o });
const dots = (c: HTMLElement) => Array.from(c.querySelectorAll<HTMLElement>(".run-tl-dots > i"));
const boxEl = (c: HTMLElement) => c.querySelector<HTMLElement>(".run-tl-box");

beforeEach(() => { Element.prototype.scrollIntoView = vi.fn(); });

describe("TimelinePhase", () => {
  it("a settled multi-agent phase starts collapsed — a summary box (name · meta · dots), no tiles, no terminal", () => {
    const { container } = render(<TimelinePhase wave={wave({ agents: [a("a1", "Succeeded", 25_000), a("a2", "Succeeded", 12_000)] })} />);

    expect(screen.getByText("Implement")).toBeInTheDocument();
    expect(screen.getByText("2 agents · 25s")).toBeInTheDocument();   // the wall-clock ≈ the longest agent
    expect(dots(container).map((d) => d.dataset.state)).toEqual(["done", "done"]);
    expect(screen.queryByTestId("tile")).toBeNull();                  // collapsed
    expect(screen.queryByTestId("terminal")).toBeNull();

    fireEvent.click(boxEl(container)!);
    expect(screen.getAllByTestId("tile")).toHaveLength(2);            // the box click drills into the tiles
  });

  it("an in-flight multi-agent phase auto-opens to its tiles, meta shows the live counts", () => {
    render(<TimelinePhase wave={wave({ agents: [a("a1", "Running"), a("a2", "Succeeded"), a("a3", "Queued")] })} />);

    expect(screen.getByText("3 agents · 1 running · 1 queued")).toBeInTheDocument();
    expect(screen.getAllByTestId("tile")).toHaveLength(3);
    expect(screen.queryByTestId("terminal")).toBeNull();             // no agent focused yet
  });

  it("summarizes a settled phase with failures as 'N failed'", () => {
    render(<TimelinePhase wave={wave({ agents: [a("a1", "Succeeded", 1000), a("a2", "Failed", 2000)] })} />);
    expect(screen.getByText("2 agents · 1 failed")).toBeInTheDocument();
  });

  it("a single-agent phase opens straight to its terminal — skips the tile layer", () => {
    render(<TimelinePhase wave={wave({ agents: [a("a1", "Running")] })} />);   // in flight → auto-open

    expect(screen.getByTestId("terminal")).toHaveTextContent("term:a1");
    expect(screen.queryByTestId("tile")).toBeNull();
    expect(screen.getByTestId("terminal").dataset.closable).toBe("true");      // its close collapses the box
  });

  it("a settled single-agent phase starts collapsed; clicking the box opens its terminal directly", () => {
    const { container } = render(<TimelinePhase wave={wave({ agents: [a("a1", "Succeeded", 9000)] })} />);

    expect(screen.queryByTestId("terminal")).toBeNull();             // collapsed
    expect(screen.getByText("1 agent · 9s")).toBeInTheDocument();    // singular "agent"

    fireEvent.click(boxEl(container)!);
    expect(screen.getByTestId("terminal")).toHaveTextContent("term:a1");
    expect(screen.queryByTestId("tile")).toBeNull();
  });

  it("lifts the selection on a tile click and opens the focused agent's terminal below", () => {
    const onSelectAgent = vi.fn();
    const { rerender } = render(<TimelinePhase wave={wave({ agents: [a("a1"), a("a2")] })} onSelectAgent={onSelectAgent} />);

    fireEvent.click(screen.getByText("a1"));
    expect(onSelectAgent).toHaveBeenCalledWith("a1");

    rerender(<TimelinePhase wave={wave({ agents: [a("a1"), a("a2")] })} selectedAgentRunId="a2" onSelectAgent={onSelectAgent} />);
    expect(screen.getByTestId("terminal")).toHaveTextContent("term:a2");
    expect(screen.getByTestId("terminal").dataset.closable).toBe("true");   // the drilled terminal closes back to the tiles
  });

  it("force-opens a collapsed (settled) phase when the outline selects one of its agents", () => {
    render(<TimelinePhase wave={wave({ agents: [a("a1", "Succeeded", 1000), a("a2", "Succeeded", 1000)] })} selectedAgentRunId="a2" />);

    expect(screen.getAllByTestId("tile")).toHaveLength(2);           // opened despite being settled
    expect(screen.getByTestId("terminal")).toHaveTextContent("term:a2");
  });

  it("a manually collapsed in-flight phase stays collapsed — the manual toggle wins over auto-open", () => {
    const { container } = render(<TimelinePhase wave={wave({ agents: [a("a1", "Running"), a("a2", "Running")] })} />);

    expect(screen.getAllByTestId("tile")).toHaveLength(2);           // active → auto-open
    fireEvent.click(boxEl(container)!);                              // user collapses it
    expect(screen.queryByTestId("tile")).toBeNull();                // stays collapsed despite still running
  });

  it("re-opens a manually collapsed phase when the outline then selects one of its agents", () => {
    const onSelectAgent = vi.fn();
    const { container, rerender } = render(<TimelinePhase wave={wave({ agents: [a("a1", "Running"), a("a2", "Running")] })} onSelectAgent={onSelectAgent} />);

    fireEvent.click(boxEl(container)!);                              // collapse (userToggle=false)
    expect(screen.queryByTestId("tile")).toBeNull();

    rerender(<TimelinePhase wave={wave({ agents: [a("a1", "Running"), a("a2", "Running")] })} selectedAgentRunId="a2" onSelectAgent={onSelectAgent} />);
    expect(screen.getByTestId("terminal")).toHaveTextContent("term:a2");   // agent-select outranks the stale collapse
  });

  it("closing a single-agent terminal collapses the box and clears the shared selection", () => {
    const onSelectAgent = vi.fn();
    render(<TimelinePhase wave={wave({ agents: [a("a1", "Running")] })} onSelectAgent={onSelectAgent} />);

    expect(screen.getByTestId("terminal")).toHaveTextContent("term:a1");   // active single → open
    fireEvent.click(screen.getByTestId("terminal"));                       // the mock fires onClose on click
    expect(onSelectAgent).toHaveBeenCalledWith(null);                      // selection cleared (outline row un-highlights)
    expect(screen.queryByTestId("terminal")).toBeNull();                   // and the box collapses
  });
});
