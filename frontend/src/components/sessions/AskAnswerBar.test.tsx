import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, describe, expect, it, vi } from "vitest";

import { sessionsApi } from "@/api/sessions";
import { RunActionsContext } from "@/components/workflows/runActionsContext";
import { AskAnswerBar } from "./SessionRoomView";

/**
 * The Room's inline answer bar is the surface that decides whether a supervisor gate is ruled on by a FIELD or by the
 * leading word of whatever the operator typed. It must send the structured verdict on EVERY card that asks for a
 * ruling — the plan confirmation, the irreversible-action approval, the review-gate escalation, the amend co-sign —
 * which is exactly what `decisionGate` carries. Keying it on the review escalation alone left two of those four cards
 * posting bare text, so a 繁中「批准」on them was still read as revision feedback.
 */
function renderBar(props: { decisionGate: boolean; escalation: boolean }) {
  const answer = vi.spyOn(sessionsApi, "answerRunAsk").mockResolvedValue({ resumed: true });

  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <RunActionsContext.Provider value={{ runId: "run-1", isTerminal: false }}>
        <AskAnswerBar {...props} />
      </RunActionsContext.Provider>
    </QueryClientProvider>,
  );

  return answer;
}

afterEach(() => vi.restoreAllMocks());

describe("AskAnswerBar", () => {
  it("sends approve as a field when the operator approves a gate card, whatever language they typed", async () => {
    const answer = renderBar({ decisionGate: true, escalation: false });

    await userEvent.click(screen.getByRole("button", { name: /approve/i }));

    expect(answer).toHaveBeenCalledWith("run-1", "approve", "approve");
  });

  it("sends revise with typed guidance on a gate card, so text starting with 'approve' cannot release it", async () => {
    const answer = renderBar({ decisionGate: true, escalation: true });

    await userEvent.type(screen.getByPlaceholderText("Describe what to do instead…"), "approve nothing until the tests pass");
    await userEvent.click(screen.getByRole("button", { name: "Answer" }));

    expect(answer).toHaveBeenCalledWith("run-1", "approve nothing until the tests pass", "revise");
  });

  it("sends no verdict on a content ask, which has nothing to approve", async () => {
    const answer = renderBar({ decisionGate: false, escalation: false });

    expect(screen.queryByRole("button", { name: /approve/i })).not.toBeInTheDocument();

    await userEvent.type(screen.getByPlaceholderText("Type your answer…"), "use the staging Postgres");
    await userEvent.click(screen.getByRole("button", { name: "Answer" }));

    expect(answer).toHaveBeenCalledWith("run-1", "use the staging Postgres", undefined);
  });

  it("frames the escalation's approve as the review absolution it is, and the other gates plainly", () => {
    renderBar({ decisionGate: true, escalation: true });
    expect(screen.getByRole("button", { name: /Approve anyway/ })).toBeInTheDocument();
  });
});
