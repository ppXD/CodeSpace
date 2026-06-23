import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

import { AgentWaveTable } from "./AgentWaveTable";

function ref(o: Partial<PhaseAgentRef> & { agentRunId: string }): PhaseAgentRef {
  return { status: "Running", ...o };
}
const rows = (c: HTMLElement) => Array.from(c.querySelectorAll<HTMLElement>(".agent-wave-row"));
const cellsOf = (r: HTMLElement) => Array.from(r.querySelectorAll("td")).map((c) => c.textContent);

describe("AgentWaveTable", () => {
  it("renders the four columns and each agent's rollup, formatted", () => {
    const { container } = render(
      <AgentWaveTable
        agents={[ref({ agentRunId: "a1", label: "backend-fix", inputTokens: 54000, outputTokens: 2600, toolCount: 16, durationMs: 137_000 })]}
        openId={null}
        onOpen={vi.fn()}
      />,
    );

    expect(screen.getByText("Agent")).toBeInTheDocument();
    expect(screen.getByText("Tokens")).toBeInTheDocument();
    expect(screen.getByText("Tools")).toBeInTheDocument();
    expect(screen.getByText("Time")).toBeInTheDocument();

    expect(screen.getByText("backend-fix")).toBeInTheDocument();
    expect(cellsOf(rows(container)[0])).toEqual(["backend-fix", "56.6k", "16", "2m 17s"]);
  });

  it("renders an em dash for a missing token / tool / duration column", () => {
    const { container } = render(<AgentWaveTable agents={[ref({ agentRunId: "a1", label: "x" })]} openId={null} onOpen={vi.fn()} />);

    expect(cellsOf(rows(container)[0])).toEqual(["x", "—", "—", "—"]);
  });

  it("shows a real 0 in the Tools column (made none), not an em dash", () => {
    const { container } = render(<AgentWaveTable agents={[ref({ agentRunId: "a1", label: "x", toolCount: 0 })]} openId={null} onOpen={vi.fn()} />);

    expect(cellsOf(rows(container)[0])[2]).toBe("0");
  });

  it("dims a done row and tones its status dot", () => {
    const { container } = render(<AgentWaveTable agents={[ref({ agentRunId: "a1", label: "x", status: "Succeeded" })]} openId={null} onOpen={vi.fn()} />);

    expect(rows(container)[0].dataset.state).toBe("done");
    expect(container.querySelector(".agent-wave-dot")!.getAttribute("data-state")).toBe("done");
  });

  it("opens an agent's terminal from the name button (a real, keyboard-accessible button)", () => {
    const onOpen = vi.fn();
    render(<AgentWaveTable agents={[ref({ agentRunId: "a1", label: "x" })]} openId={null} onOpen={onOpen} />);

    fireEvent.click(screen.getByRole("button", { name: "x" }));
    expect(onOpen).toHaveBeenCalledTimes(1);
  });

  it("keeps the row a real table row (no role override) and marks the selected + open rows", () => {
    const { container } = render(
      <AgentWaveTable
        agents={[ref({ agentRunId: "a1", label: "x" }), ref({ agentRunId: "a2", label: "y" })]}
        selectedAgentRunId="a1"
        openId="a2"
        onOpen={vi.fn()}
      />,
    );

    expect(rows(container)[0].getAttribute("role")).toBeNull();   // the cells keep their column-header association
    expect(rows(container)[0].dataset.selected).toBe("true");
    expect(rows(container)[1].dataset.open).toBe("true");
    expect(screen.getByRole("button", { name: "y" }).getAttribute("aria-expanded")).toBe("true");
  });
});
