import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PhaseAgentRef } from "@/api/workflows";

import { AgentFleetDots } from "./AgentFleetDots";

const a = (id: string, status: string, label?: string): PhaseAgentRef => ({ agentRunId: id, status, label });
const dots = (c: HTMLElement) => Array.from(c.querySelectorAll<HTMLElement>(".agent-fleet-dot"));

describe("AgentFleetDots", () => {
  it("renders one status-colored dot per agent (pending gray · running · done · failed)", () => {
    const { container } = render(<AgentFleetDots agents={[a("a1", "Running"), a("a2", "Succeeded"), a("a3", "Queued"), a("a4", "Failed")]} openId={null} onOpen={vi.fn()} />);

    expect(dots(container).map((d) => d.dataset.state)).toEqual(["running", "done", "waiting", "failed"]);
  });

  it("labels each dot with the agent name + state for screen readers", () => {
    render(<AgentFleetDots agents={[a("a1", "Succeeded", "reviewer")]} openId={null} onOpen={vi.fn()} />);

    expect(screen.getByRole("button", { name: "reviewer — done" })).toBeInTheDocument();
  });

  it("opens that agent's terminal on click", () => {
    const onOpen = vi.fn();
    render(<AgentFleetDots agents={[a("a1", "Running")]} openId={null} onOpen={onOpen} />);

    fireEvent.click(screen.getByRole("button"));
    expect(onOpen).toHaveBeenCalledWith("a1");
  });

  it("rings the selected + open dots", () => {
    const { container } = render(<AgentFleetDots agents={[a("a1", "Running"), a("a2", "Running")]} selectedAgentRunId="a1" openId="a2" onOpen={vi.fn()} />);

    expect(dots(container)[0].dataset.selected).toBe("true");
    expect(dots(container)[1].dataset.open).toBe("true");
    expect(dots(container)[1].getAttribute("aria-expanded")).toBe("true");   // matches the row/tile disclosure semantics
  });
});
