import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { PendingDecision } from "@/api/workflows";

import { DecisionCard } from "./DecisionCard";

const { mutate } = vi.hoisted(() => ({ mutate: vi.fn() }));

vi.mock("@/hooks/use-workflows", () => ({
  useAnswerDecision: () => ({ mutate, isPending: false }),
}));

function decision(o: Partial<PendingDecision>): PendingDecision {
  return {
    id: "d1", grain: "tool_ledger", rootTraceId: "run-1", decisionType: "confirm", question: "Proceed?",
    options: [], riskLevel: "low", policy: "human_required", createdAt: "2026-06-22T00:00:00Z", ...o,
  };
}

/** The first arg the card passed to mutate (the {decisionId, body} vars); the 2nd is the callbacks object. */
function lastBody() {
  return mutate.mock.calls.at(-1)?.[0];
}

describe("DecisionCard", () => {
  it("answers a single-choice decision on one click, posting the chosen option id", () => {
    mutate.mockClear();
    render(<DecisionCard decision={decision({ decisionType: "choose_one", options: [
      { id: "a", label: "Rebase" }, { id: "b", label: "Squash" },
    ] })} />);

    fireEvent.click(screen.getByText("Squash"));
    expect(lastBody()).toEqual({ decisionId: "d1", body: { selectedOptions: ["b"] } });
  });

  it("marks the recommended option and renders a side-effecting one as a danger button", () => {
    render(<DecisionCard decision={decision({ decisionType: "approve_action", recommendedOption: "go", options: [
      { id: "go", label: "Merge", isSideEffecting: true }, { id: "no", label: "Hold" },
    ] })} />);

    expect(screen.getByText("recommended")).toBeTruthy();
    expect(screen.getByText("Merge").closest("button")?.className).toContain("btn-danger");
    expect(screen.getByText("Hold").closest("button")?.className).toContain("btn-primary");
  });

  it("composes a choose_many answer and only submits the checked options", () => {
    mutate.mockClear();
    render(<DecisionCard decision={decision({ decisionType: "choose_many", options: [
      { id: "x", label: "Lint" }, { id: "y", label: "Test" }, { id: "z", label: "Build" },
    ] })} />);

    const submit = screen.getByText("Submit");
    expect(submit.hasAttribute("disabled")).toBe(true);          // nothing checked yet

    fireEvent.click(screen.getByText("Lint"));
    fireEvent.click(screen.getByText("Build"));
    fireEvent.click(submit);
    expect(lastBody()).toEqual({ decisionId: "d1", body: { selectedOptions: ["x", "z"] } });
  });

  it("submits a free-text answer, trimmed, and stays disabled while empty", () => {
    mutate.mockClear();
    render(<DecisionCard decision={decision({ decisionType: "free_text", options: [] })} />);

    const submit = screen.getByText("Submit");
    expect(submit.hasAttribute("disabled")).toBe(true);

    fireEvent.change(screen.getByPlaceholderText("Type your answer…"), { target: { value: "  use the staging mirror  " } });
    fireEvent.click(submit);
    expect(lastBody()).toEqual({ decisionId: "d1", body: { freeText: "use the staging mirror" } });
  });

  it("falls back to free text for an options-less single-choice decision (a confirm node with no choices)", () => {
    mutate.mockClear();
    // The node grain defaults decisionType to `confirm` and may carry no options — the backend then accepts a
    // free-text answer. Projecting buttons over [] would render an unanswerable card; we offer the textarea instead.
    render(<DecisionCard decision={decision({ decisionType: "confirm", options: [] })} />);

    const box = screen.getByPlaceholderText("Type your answer…");
    expect(box).toBeTruthy();
    fireEvent.change(box, { target: { value: "yes, proceed" } });
    fireEvent.click(screen.getByText("Submit"));
    expect(lastBody()).toEqual({ decisionId: "d1", body: { freeText: "yes, proceed" } });
  });

  it("shows the risk badge + a deadline countdown", () => {
    const future = new Date(Date.now() + 8 * 60_000).toISOString();
    render(<DecisionCard decision={decision({ riskLevel: "high", deadlineAt: future, options: [{ id: "a", label: "OK" }] })} />);

    expect(screen.getByText("high risk")).toBeTruthy();
    expect(screen.getByText(/left$/)).toBeTruthy();
  });
});
