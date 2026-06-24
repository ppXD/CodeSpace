import { fireEvent, render } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef, RunPhase } from "@/api/workflows";

import { RunOutline } from "./RunOutline";

// A node-spine phase (the structural manual/code/end) — the default, since the outline renders the node spine.
function phase(o: Partial<RunPhase>): RunPhase {
  return {
    id: "p", label: "P", kind: "node", status: "Pending", order: 0,
    agents: [], metrics: { agentCount: 0, succeededCount: 0, failedCount: 0 }, sourceKey: "node-summary",
    ...o,
  };
}
const agent = (id: string, status: string, label: string, nodeId?: string): PhaseAgentRef => ({ agentRunId: id, status, label, nodeId });

describe("RunOutline", () => {
  it("shows an empty hint when there are no phases", () => {
    const { container } = render(<RunOutline phases={[]} />);
    expect(container.querySelector(".run-outline-empty")).not.toBeNull();
  });

  it("renders the active phase highlighted with a spinner, its done/total roll-up, and its agent children", () => {
    const { container, getByText } = render(<RunOutline phases={[
      phase({ id: "code", label: "Implement", status: "Active", agents: [agent("a1", "Running", "backend-fix"), agent("a2", "Succeeded", "frontend-fix")] }),
    ]} />);

    expect(getByText("Implement")).toBeTruthy();
    expect(container.querySelector('.run-outline-phase[data-status="active"]')).not.toBeNull();
    expect(container.querySelector('.run-outline-glyph[data-status="active"] .run-outline-spin')).not.toBeNull();
    expect(getByText("1/2")).toBeTruthy();   // done / total, off the displayed agents
    expect(getByText("backend-fix")).toBeTruthy();   // the active phase auto-expands its agents
    expect(container.querySelector(".run-outline-agent-dot[data-busy]")).not.toBeNull();
  });

  it("maps each terminal status to its glyph tone", () => {
    const { container } = render(<RunOutline phases={[
      phase({ id: "ok", label: "Plan", status: "Succeeded" }),
      phase({ id: "bad", label: "Verify", status: "Failed" }),
      phase({ id: "wait", label: "Approve", status: "Waiting" }),
    ]} />);
    expect(container.querySelector('.run-outline-glyph[data-status="succeeded"]')).not.toBeNull();
    expect(container.querySelector('.run-outline-glyph[data-status="failed"]')).not.toBeNull();
    expect(container.querySelector('.run-outline-glyph[data-status="waiting"]')).not.toBeNull();
  });

  it("surfaces a phase summary line when present", () => {
    const { getByText } = render(<RunOutline phases={[phase({ id: "ask", label: "Choose strategy", status: "Waiting", summary: "Squash or rebase? — awaiting answer" })]} />);
    expect(getByText("Squash or rebase? — awaiting answer")).toBeTruthy();
  });

  it("selects a phase when its label is clicked, marking it, and toggles off on a re-click", () => {
    const onSelectPhase = vi.fn();
    const { getByText, container, rerender } = render(<RunOutline phases={[phase({ id: "code", label: "Implement", status: "Active", agents: [agent("a1", "Running", "x")] })]} onSelectPhase={onSelectPhase} />);

    fireEvent.click(getByText("Implement"));
    expect(onSelectPhase).toHaveBeenCalledWith("code");

    rerender(<RunOutline phases={[phase({ id: "code", label: "Implement", status: "Active", agents: [agent("a1", "Running", "x")] })]} selectedPhaseId="code" onSelectPhase={onSelectPhase} />);
    expect(container.querySelector('.run-outline-phase[data-selected]')).not.toBeNull();
    fireEvent.click(getByText("Implement"));
    expect(onSelectPhase).toHaveBeenLastCalledWith(null);
  });

  it("collapses a non-active phase's agents until the caret expands them", () => {
    const { container, getByText, getByLabelText } = render(<RunOutline phases={[
      phase({ id: "ground", label: "Ground", status: "Succeeded", agents: [agent("a1", "Succeeded", "scout")] }),
    ]} onSelectPhase={vi.fn()} />);

    expect(container.querySelector(".run-outline-agent")).toBeNull();   // a done phase starts collapsed
    fireEvent.click(getByLabelText("Toggle agents"));
    expect(getByText("scout")).toBeTruthy();
  });

  it("selects an agent's phase AND the agent when an agent row is clicked", () => {
    const onSelectPhase = vi.fn();
    const onSelectAgent = vi.fn();
    const { getByText, container } = render(<RunOutline
      phases={[phase({ id: "code", label: "Implement", status: "Active", agents: [agent("a1", "Running", "backend-fix"), agent("a2", "Queued", "frontend-fix")] })]}
      selectedAgentRunId="a2"
      onSelectPhase={onSelectPhase}
      onSelectAgent={onSelectAgent}
    />);

    fireEvent.click(getByText("backend-fix"));
    expect(onSelectPhase).toHaveBeenCalledWith("code");   // focusing an agent focuses its phase too
    expect(onSelectAgent).toHaveBeenCalledWith("a1");
    expect(container.querySelector(".run-outline-agent[data-selected]")?.textContent).toContain("frontend-fix");
  });

  it("nests a supervisor's semantic phases under a 'Phases' block beneath their node", () => {
    const { container, getByText } = render(<RunOutline phases={[
      phase({ id: "manual", label: "manual", status: "Succeeded" }),
      phase({ id: "code", label: "code", status: "Active" }),   // the supervisor node — no inline agents
      phase({ id: "end", label: "end", status: "Pending" }),
      phase({ id: "phase-impl", label: "Implement", kind: "phase", status: "Active", sourceKey: "supervisor-ledger", agents: [agent("a1", "Running", "backend", "code")] }),
    ]} onSelectPhase={vi.fn()} />);

    const block = container.querySelector(".run-outline-phases");
    expect(block).not.toBeNull();
    expect(getByText("Phases")).toBeTruthy();
    expect(block?.textContent).toContain("Implement");   // the semantic phase lives in the block, not the spine
  });

  it("shows spawn-decision agents in the Phases block when no semantic phases were authored (flat plan)", () => {
    const { container, getByText } = render(<RunOutline phases={[
      phase({ id: "code", label: "code", status: "Active" }),
      phase({ id: "decision-2", label: "Spawn 2 agents", kind: "spawn", status: "Active", sourceKey: "supervisor-ledger", agents: [agent("a1", "Running", "worker-1"), agent("a2", "Running", "worker-2")] }),
    ]} onSelectPhase={vi.fn()} />);

    const block = container.querySelector(".run-outline-phases");
    expect(block?.textContent).toContain("Spawn 2 agents");
    expect(getByText("worker-1")).toBeTruthy();   // the decision's agents are NOT dropped from the outline
  });

  it("does not make an agentless phase selectable (its label stays a plain span)", () => {
    const { container } = render(<RunOutline phases={[phase({ id: "code", label: "code", status: "Active" })]} onSelectPhase={vi.fn()} />);
    expect(container.querySelector("button.run-outline-label")).toBeNull();   // no agents → nothing to filter to
    expect(container.querySelector(".run-outline-label")?.textContent).toBe("code");
  });

  it("renders a queued agent's dot as amber (data-status), never the busy-blue pulse", () => {
    const { container } = render(<RunOutline phases={[phase({ id: "code", label: "code", status: "Active", agents: [agent("a1", "Queued", "deploy")] })]} onSelectAgent={vi.fn()} />);

    const dot = container.querySelector(".run-outline-agent-dot");
    expect(dot?.getAttribute("data-status")).toBe("queued");
    expect(dot?.hasAttribute("data-busy")).toBe(false);   // a queued agent isn't "busy" → it reads amber, not the running blue
  });

  it("renders agent rows as plain (non-button) when no handler is given", () => {
    const { container } = render(<RunOutline phases={[phase({ status: "Active", agents: [agent("a1", "Running", "solo")] })]} />);
    expect(container.querySelector("button.run-outline-agent")).toBeNull();
    expect(container.querySelector(".run-outline-agent")).not.toBeNull();
  });
});
