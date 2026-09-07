import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";

import type { RoomTurnAttempt } from "@/api/sessions";
import { TurnAttempts } from "./SessionRoomView";

/**
 * Item 7.2 — a rerun used to tell the reader only THAT a retry happened, never WHAT changed. These pin the ladder's
 * per-attempt "since previous attempt" line: it renders ONLY the backend-computed fields that actually differ, and
 * stays silent (no line at all) for the first attempt and for any rung the backend found nothing to say about.
 */
function attempt(over: Partial<RoomTurnAttempt> = {}): RoomTurnAttempt {
  return {
    runId: "11111111-1111-1111-1111-111111111111",
    attemptNumber: 1,
    status: "Failure",
    at: "2026-09-06T07:00:00Z",
    isCurrent: false,
    ...over,
  };
}

async function openLadder() {
  await userEvent.click(screen.getByTitle("Switch attempt"));
}

describe("the attempt ladder's since-previous-attempt line", () => {
  it("shows the escalated model, the recovered outcome, the flipped check and the costlier spend", async () => {
    const original = attempt({ runId: "a1", attemptNumber: 1, status: "Failure" });
    const rerun = attempt({
      runId: "a2",
      attemptNumber: 2,
      status: "Success",
      isCurrent: true,
      delta: { model: "claude-opus-4-8", outcome: "Success", acceptancePassed: false, acceptanceDetail: "tests-failed-exit-1", costDeltaUsd: 0.0245 },
    });

    render(<TurnAttempts attempts={[original, rerun]} nowMs={Date.now()} onOpenRun={() => {}} />);
    await openLadder();

    expect(screen.getByText("since previous:")).toBeInTheDocument();
    expect(screen.getByText("model claude-opus-4-8")).toBeInTheDocument();
    expect(screen.getByText("· Done")).toBeInTheDocument();
    expect(screen.getByText("· checks failed")).toBeInTheDocument();
    expect(screen.getByText("· +$0.025")).toBeInTheDocument();
  });

  it("never renders a line for the first attempt — nothing precedes it", async () => {
    const original = attempt({ runId: "a1", attemptNumber: 1 });
    const rerun = attempt({ runId: "a2", attemptNumber: 2, isCurrent: true, delta: { model: "claude-opus-4-8" } });

    const { container } = render(<TurnAttempts attempts={[original, rerun]} nowMs={Date.now()} onOpenRun={() => {}} />);
    await openLadder();

    const rows = container.querySelectorAll(".room-attempts-item");
    expect(rows).toHaveLength(2);
    expect(rows[0].querySelector(".room-attempt-delta")).toBeNull();
    expect(rows[1].querySelector(".room-attempt-delta")).not.toBeNull();
  });

  it("stays silent when the backend found nothing comparable to say", async () => {
    const original = attempt({ runId: "a1", attemptNumber: 1 });
    const rerun = attempt({ runId: "a2", attemptNumber: 2, isCurrent: true, delta: null });

    const { container } = render(<TurnAttempts attempts={[original, rerun]} nowMs={Date.now()} onOpenRun={() => {}} />);
    await openLadder();

    expect(container.querySelector(".room-attempt-delta")).toBeNull();
    expect(screen.queryByText("since previous:")).toBeNull();
  });

  it("renders only the ONE field the backend flagged, not the others", async () => {
    const original = attempt({ runId: "a1", attemptNumber: 1 });
    const rerun = attempt({ runId: "a2", attemptNumber: 2, isCurrent: true, delta: { costDeltaUsd: -0.01 } });

    render(<TurnAttempts attempts={[original, rerun]} nowMs={Date.now()} onOpenRun={() => {}} />);
    await openLadder();

    expect(screen.getByText("-$0.010")).toBeInTheDocument();
    expect(screen.queryByText(/model /)).toBeNull();
    expect(screen.queryByText(/checks (passed|failed)/)).toBeNull();
  });

  it("names a completion park through the SAME override the rung's own status pill uses, never the raw 'Waiting'", async () => {
    const original = attempt({ runId: "a1", attemptNumber: 1, status: "Suspended" });
    const rerun = attempt({ runId: "a2", attemptNumber: 2, status: "Suspended", statusWord: "Parked", isCurrent: true, delta: { outcome: "Suspended" } });

    const { container } = render(<TurnAttempts attempts={[original, rerun]} nowMs={Date.now()} onOpenRun={() => {}} />);
    await openLadder();

    const deltaLine = container.querySelector(".room-attempt-delta");
    expect(deltaLine?.textContent).toContain("Parked");
    expect(deltaLine?.textContent).not.toContain("Waiting");
  });

  it("opens the picked attempt's run and never the shown one's", async () => {
    const onOpenRun = vi.fn();
    const original = attempt({ runId: "a1", attemptNumber: 1 });
    const rerun = attempt({ runId: "a2", attemptNumber: 2, isCurrent: true, delta: { model: "claude-opus-4-8" } });

    render(<TurnAttempts attempts={[original, rerun]} nowMs={Date.now()} onOpenRun={onOpenRun} />);
    await openLadder();

    const [firstRow] = screen.getAllByRole("menuitem");
    await userEvent.click(firstRow);

    expect(onOpenRun).toHaveBeenCalledWith("a1");
  });
});
