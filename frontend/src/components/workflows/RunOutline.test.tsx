import { render } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { RunPhase } from "@/api/workflows";

import { RunOutline } from "./RunOutline";

function phase(o: Partial<RunPhase>): RunPhase {
  return {
    id: "p", label: "P", kind: "node", status: "Pending", order: 0,
    agents: [], metrics: { agentCount: 0, succeededCount: 0, failedCount: 0 }, sourceKey: "s",
    ...o,
  };
}

describe("RunOutline", () => {
  it("shows an empty hint when there are no phases", () => {
    const { container } = render(<RunOutline phases={[]} />);
    expect(container.querySelector(".run-outline-empty")).not.toBeNull();
  });

  it("renders the active phase highlighted with a spinner, its roll-up, and its agent children", () => {
    const { container, getByText } = render(<RunOutline phases={[
      phase({ id: "impl", label: "Implement", status: "Active", metrics: { agentCount: 2, succeededCount: 1, failedCount: 0 }, agents: [
        { agentRunId: "a1", label: "backend-fix", status: "Running" },
        { agentRunId: "a2", label: "frontend-fix", status: "Succeeded" },
      ] }),
    ]} />);
    expect(getByText("Implement")).toBeTruthy();
    expect(container.querySelector('.run-outline-phase[data-status="active"]')).not.toBeNull();
    expect(container.querySelector('.run-outline-glyph[data-status="active"] .run-outline-spin')).not.toBeNull();
    expect(getByText("1/2")).toBeTruthy();                       // succeeded / total roll-up
    expect(getByText("backend-fix")).toBeTruthy();
    expect(container.querySelector(".run-outline-agent-dot[data-busy]")).not.toBeNull();   // the Running agent pulses
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

  it("surfaces a phase summary line when present (e.g. an ask_human Q+A)", () => {
    const { getByText } = render(<RunOutline phases={[
      phase({ id: "ask", label: "Choose merge strategy", status: "Waiting", summary: "Squash or rebase? — awaiting answer" }),
    ]} />);
    expect(getByText("Squash or rebase? — awaiting answer")).toBeTruthy();
  });
});
