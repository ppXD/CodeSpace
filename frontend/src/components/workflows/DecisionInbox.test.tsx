import { render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PendingDecision } from "@/api/workflows";

import { DecisionInbox } from "./DecisionInbox";

// The card children answer through this hook — stub it so the inbox renders without a QueryClient.
vi.mock("@/hooks/use-workflows", () => ({
  useAnswerDecision: () => ({ mutate: vi.fn(), isPending: false }),
}));

function decision(o: Partial<PendingDecision>): PendingDecision {
  return {
    id: "d", grain: "tool_ledger", rootTraceId: "run-1", decisionType: "free_text", question: "?",
    options: [], riskLevel: "low", policy: "human_required", createdAt: "2026-06-22T00:00:00Z", ...o,
  };
}

describe("DecisionInbox", () => {
  it("shows a calm all-clear when nothing is parked", () => {
    const { container } = render(<DecisionInbox decisions={[]} />);
    expect(container.querySelector(".decision-inbox-clear")).not.toBeNull();
    expect(container.querySelector(".decision-inbox-count")).toBeNull();   // no badge at zero
  });

  it("lists the run's decisions with a count badge and one card each", () => {
    const { container } = render(<DecisionInbox decisions={[
      decision({ id: "a", question: "Pick a merge strategy" }),
      decision({ id: "b", question: "Approve the deploy" }),
    ]} />);

    expect(container.querySelector(".decision-inbox-count")?.textContent).toBe("2");
    expect(container.querySelectorAll(".decision-card").length).toBe(2);
    expect(screen.getByText("Pick a merge strategy")).toBeTruthy();
    expect(screen.getByText("Approve the deploy")).toBeTruthy();
  });
});
