import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { AgentGroupBlock, RoomAgentCard } from "@/api/sessions";
import { AgentSection } from "./SessionRoomView";

const card = (overrides: Partial<RoomAgentCard> = {}): RoomAgentCard => ({
  agentRunId: "r1",
  label: "Rename the command",
  status: "Succeeded",
  ...overrides,
});

const group = (...agents: RoomAgentCard[]): AgentGroupBlock => ({
  id: "turn-1:agents",
  seq: 3,
  type: "agent_group",
  title: "Agents",
  agents,
});

/** The Room's OWN agent_group lane — the one a plain single-agent / flow.map run falls back to when no decision
 *  beat carries its agents. Its card is the only place such a run's failure reason can reach the reader. */
describe("the room's own agent card names why a run failed", () => {
  it("renders the error line for a failed card", () => {
    const { container } = render(<AgentSection group={group(card({ status: "Failed", error: "litellm.BadRequestError: Unexpected message role" }))} />);

    expect(container.querySelector(".room-arow-err")).toBeInTheDocument();
    expect(screen.getByText(/litellm.BadRequestError: Unexpected message role/)).toBeInTheDocument();
  });

  it("renders the error line for a NeedsReview card — the backend fills the reason for ANY non-succeeded agent", () => {
    // The mutation this catches: gating the line on the FAILED_AGENT tone set (Failed/Cancelled/TimedOut) silently
    // drops a reason the backend did carry, on exactly the card a reader opened to find out what went wrong.
    const { container } = render(<AgentSection group={group(card({ status: "NeedsReview", error: "acceptance command exited 1" }))} />);

    expect(container.querySelector(".room-arow-err")).toBeInTheDocument();
    expect(screen.getByText(/acceptance command exited 1/)).toBeInTheDocument();
  });

  it("renders no error line for a succeeded card", () => {
    const { container } = render(<AgentSection group={group(card())} />);

    expect(container.querySelector(".room-arow-err")).not.toBeInTheDocument();
  });

  it("keeps the row's own tone driven by status, not by the presence of an error", () => {
    const { container } = render(<AgentSection group={group(card({ agentRunId: "r1", status: "Failed", error: "boom" }), card({ agentRunId: "r2", status: "NeedsReview", error: "acceptance command exited 1" }))} />);

    expect(container.querySelector(".room-adot-err")).toBeInTheDocument();
    expect(container.querySelector(".room-adot-ok")).toBeInTheDocument();
    expect(container.querySelectorAll(".room-arow-err").length).toBe(2);
  });
});
