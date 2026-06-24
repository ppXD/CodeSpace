import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef, RunPhase } from "@/api/workflows";

import { RunOutline } from "./RunOutline";

function phase(o: Partial<RunPhase>): RunPhase {
  return {
    id: "p", label: "P", kind: "node", status: "Pending", order: 0,
    agents: [], metrics: { agentCount: 0, succeededCount: 0, failedCount: 0 }, sourceKey: "node-summary",
    ...o,
  };
}
const agent = (id: string, status: string, label: string, nodeId?: string, durationMs?: number): PhaseAgentRef => ({ agentRunId: id, status, label, nodeId, durationMs });
const box = (c: HTMLElement) => c.querySelector<HTMLElement>(".run-outline-box");
const boxHead = (c: HTMLElement) => c.querySelector<HTMLElement>(".run-outline-box-head");   // the toggle lives on the header button, not the gray card div
const dots = (c: HTMLElement) => Array.from(c.querySelectorAll<HTMLElement>(".run-outline-dots > i")).map((d) => d.dataset.state);

describe("RunOutline", () => {
  it("shows an empty hint when there are no phases", () => {
    const { container } = render(<RunOutline phases={[]} />);
    expect(container.querySelector(".run-outline-empty")).not.toBeNull();
  });

  it("renders an agent-bearing phase as a framed box with square per-agent dots + an Agent·Time list on expand", () => {
    const { container, getByText } = render(<RunOutline phases={[
      phase({ id: "code", label: "Implement", status: "Active", agents: [agent("a1", "Running", "backend-fix", undefined, 137_000), agent("a2", "Succeeded", "frontend-fix")] }),
    ]} onSelectPhase={vi.fn()} />);

    expect(box(container)).not.toBeNull();
    expect(dots(container)).toEqual(["running", "done"]);
    expect(getByText("Implement")).toBeTruthy();
    expect(getByText("backend-fix")).toBeTruthy();   // the Active phase auto-expands its list
    expect(getByText("2m 17s")).toBeTruthy();        // the Time column
  });

  it("maps each terminal status to its glyph tone on the agentless node rows", () => {
    const { container } = render(<RunOutline phases={[
      phase({ id: "ok", label: "Plan", status: "Succeeded" }),
      phase({ id: "bad", label: "Verify", status: "Failed" }),
      phase({ id: "wait", label: "Approve", status: "Waiting" }),
    ]} />);
    expect(container.querySelector('.run-outline-glyph[data-status="succeeded"]')).not.toBeNull();
    expect(container.querySelector('.run-outline-glyph[data-status="failed"]')).not.toBeNull();
    expect(container.querySelector('.run-outline-glyph[data-status="waiting"]')).not.toBeNull();
  });

  it("clicking the box header selects the phase and toggles its list; a done phase starts collapsed", () => {
    const onSelectPhase = vi.fn();
    const { container, getByText } = render(<RunOutline phases={[phase({ id: "code", label: "Implement", status: "Succeeded", agents: [agent("a1", "Succeeded", "scout")] })]} onSelectPhase={onSelectPhase} />);

    expect(container.querySelector(".run-outline-aglist")).toBeNull();   // a done phase starts collapsed
    fireEvent.click(boxHead(container)!);
    expect(onSelectPhase).toHaveBeenCalledWith("code");
    expect(getByText("scout")).toBeTruthy();   // the box-header click expanded the list — the list lives INSIDE the gray card
    expect(box(container)!.querySelector(".run-outline-aglist")).not.toBeNull();
    expect(container.querySelector(".run-outline-phase[data-selected]")).toBeNull();   // the gray card never gains a persistent selected highlight — same whether clicked or not
  });

  it("clicking an agent name focuses its phase AND the agent", () => {
    const onSelectPhase = vi.fn();
    const onSelectAgent = vi.fn();
    const { getByText, container } = render(<RunOutline
      phases={[phase({ id: "code", label: "Implement", status: "Active", agents: [agent("a1", "Running", "backend-fix"), agent("a2", "Queued", "frontend-fix")] })]}
      selectedAgentRunId="a2"
      onSelectPhase={onSelectPhase}
      onSelectAgent={onSelectAgent}
    />);

    fireEvent.click(getByText("backend-fix"));   // clicking anywhere in the row (the name span bubbles to the row button) focuses it
    expect(onSelectPhase).toHaveBeenCalledWith("code");
    expect(onSelectAgent).toHaveBeenCalledWith("a1");
    expect(container.querySelector(".run-outline-agrow[data-selected]")?.textContent).toContain("frontend-fix");
  });

  it("renders a queued agent's box dot as amber (data-state waiting), with no data-busy", () => {
    const { container } = render(<RunOutline phases={[phase({ id: "code", label: "code", status: "Active", agents: [agent("a1", "Queued", "deploy")] })]} onSelectPhase={vi.fn()} />);

    const dot = container.querySelector<HTMLElement>(".run-outline-dots > i");
    expect(dot?.dataset.state).toBe("waiting");
    expect(dot?.hasAttribute("data-busy")).toBe(false);
  });

  it("shows spawn-decision agents in the Phases block when no semantic phases were authored (flat plan)", () => {
    const { container, getByText } = render(<RunOutline phases={[
      phase({ id: "code", label: "code", status: "Active" }),
      phase({ id: "decision-2", label: "Spawn 2 agents", kind: "spawn", status: "Active", sourceKey: "supervisor-ledger", agents: [agent("a1", "Running", "worker-1"), agent("a2", "Running", "worker-2")] }),
    ]} onSelectPhase={vi.fn()} />);

    const block = container.querySelector(".run-outline-phases");
    expect(block?.textContent).toContain("Spawn 2 agents");
    expect(getByText("worker-1")).toBeTruthy();
  });

  it("renders an agentless node as a plain row, not a selectable box", () => {
    const { container } = render(<RunOutline phases={[phase({ id: "code", label: "code", status: "Active" })]} onSelectPhase={vi.fn()} />);
    expect(container.querySelector(".run-outline-box")).toBeNull();
    expect(container.querySelector(".run-outline-label")?.textContent).toBe("code");
  });

  it("renders an agent row as plain (non-button) when no select handler is given", () => {
    const { container } = render(<RunOutline phases={[phase({ id: "code", status: "Active", agents: [agent("a1", "Running", "solo")] })]} />);
    expect(container.querySelector("button.run-outline-agrow")).toBeNull();   // the whole row is a div, not a clickable button
    expect(container.querySelector(".run-outline-agname")?.textContent).toBe("solo");
  });
});
