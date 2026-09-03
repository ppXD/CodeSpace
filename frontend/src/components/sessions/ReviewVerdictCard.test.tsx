import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";

import type { JournalReviewVerdict } from "@/api/sessions";
import { ReviewVerdictCard } from "./SessionRoomView";

const verdict = (over: Partial<JournalReviewVerdict> = {}): JournalReviewVerdict => ({
  approved: true,
  rationale: "sound",
  issues: [],
  scope: "decision",
  ...over,
});

/** The independence line lives behind the card's toggle — open it the way a reader would. */
async function openCard(review: JournalReviewVerdict) {
  render(<ReviewVerdictCard review={review} />);
  await userEvent.click(screen.getByRole("button", { expanded: false }));
}

describe("ReviewVerdictCard independence line", () => {
  it("names the model a model critic ran on instead of 'a second AI'", async () => {
    await openCard(verdict({ reviewerModel: "claude-sonnet-4-6" }));

    expect(screen.getByText("claude-sonnet-4-6")).toBeInTheDocument();
    expect(screen.getByText(/an independent decision review/)).toBeInTheDocument();
    expect(screen.queryByText("a second AI")).not.toBeInTheDocument();
  });

  it("drops the word 'independent' when the reviewer ran on the producer's own model", async () => {
    // A one-model pool legitimately reviews on the producer's model. Calling that "independent" is the claim this
    // card used to make about a review that was never a second opinion.
    await openCard(verdict({ reviewerModel: "claude-opus-4-8", sameModelAsProducer: true }));

    expect(screen.getByText("claude-opus-4-8")).toBeInTheDocument();
    expect(screen.getByText(/the producer's own model, independently prompted — not a second opinion/)).toBeInTheDocument();
    expect(screen.queryByText(/an independent decision review/)).not.toBeInTheDocument();
  });

  it("falls back to the old copy for a verdict that names no reviewer", async () => {
    // Every pre-existing verdict has no reviewer model — it must read exactly as before, never as a same-model claim.
    await openCard(verdict());

    expect(screen.getByText("a second AI")).toBeInTheDocument();
    expect(screen.getByText(/an independent decision review/)).toBeInTheDocument();
  });

  it("keeps the agent-reviewer line, which carries its own harness attribution", async () => {
    await openCard(verdict({ reviewerRunId: "00000000-0000-0000-0000-000000000001", reviewerHarness: "claude-code", scope: "plan" }));

    expect(screen.getByText("independent agent · claude-code")).toBeInTheDocument();
    expect(screen.queryByText("a second AI")).not.toBeInTheDocument();
  });
});
