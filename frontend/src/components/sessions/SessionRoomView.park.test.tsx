import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";

import type { AssistantTurnBlock, DiagnosticBlock, RoomAction } from "@/api/sessions";
import { DialogProvider } from "@/components/dialog/dialog-context";
import { ErrorCard, LiveRunBar, TurnActions, turnHeaderWord, turnMeta } from "./SessionRoomView";

/**
 * The completion-authority PARK — the default outcome of an unverified supervisor stop. It is `Suspended`, exactly
 * like an approval wait, and before this it rendered as one: the header said "Waiting", the authority's reason
 * appeared nowhere, and Continue was disabled — so the run sat forever with no operable exit.
 *
 * Every word here is the BACKEND's. These pin that the FE renders them and gates the exit on the backend's own
 * capability, never on a status it re-interprets locally.
 */
const parkReason = "completion-authority: Park — required stage(s) without evidence for mode 'supervisor': Integrate";

function parkCard(over: Partial<DiagnosticBlock> = {}): DiagnosticBlock {
  return {
    id: "turn-1:park",
    seq: 9,
    type: "diagnostic",
    tone: "Info",
    title: "Parked — completion not verified",
    text: "Park — required stage(s) without evidence for mode 'supervisor': Integrate. Nothing was delivered and no terminal was stamped, so the work is still resumable. Continue gives the run another turn to produce what is missing, publish it, or ask you a question — and if it still cannot, it stops honestly instead of claiming success. Stop the run to end it here instead.",
    rawDetail: parkReason,
    ...over,
  };
}

function turn(over: Partial<AssistantTurnBlock> = {}): AssistantTurnBlock {
  return {
    id: "turn-1",
    seq: 9,
    type: "assistant_turn",
    turnIndex: 1,
    turnRunId: "11111111-1111-1111-1111-111111111111",
    runId: "11111111-1111-1111-1111-111111111111",
    status: "Suspended",
    blocks: [],
    actions: [],
    ...over,
  };
}

function action(over: Partial<RoomAction> = {}): RoomAction {
  return { kind: "Continue", label: "Continue", enabled: true, ...over };
}

function renderActions(actions: RoomAction[]) {
  render(
    <QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
      <DialogProvider>
        <TurnActions actions={actions} turn={turn({ actions })} onOpenRun={() => {}} />
      </DialogProvider>
    </QueryClientProvider>,
  );
}

describe("completion-authority park", () => {
  it("renders the authority's own words, with the verbatim reason behind the raw toggle", async () => {
    render(<ErrorCard diag={parkCard()} />);

    expect(screen.getByText("Parked — completion not verified")).toBeInTheDocument();
    // The stage the authority found no evidence for is the whole point — an operator cannot fix what they cannot read.
    expect(screen.getByText(/Integrate/)).toBeInTheDocument();
    expect(screen.getByText(/Continue gives the run another turn/)).toBeInTheDocument();

    await userEvent.click(screen.getByText("Show raw error"));
    expect(screen.getByText(parkReason)).toBeInTheDocument();
  });

  it("wears the warm attention palette, not the error red — nothing failed here", () => {
    const { container, rerender } = render(<ErrorCard diag={parkCard()} />);
    expect(container.querySelector(".room-err-attn")).not.toBeNull();

    rerender(<ErrorCard diag={parkCard({ tone: "Error", title: "Authentication failed" })} />);
    expect(container.querySelector(".room-err-attn")).toBeNull();
  });

  it("shows Continue only when the backend says the turn is continuable", () => {
    renderActions([action({ enabled: true })]);
    expect(screen.getByRole("button", { name: /Continue/ })).toBeInTheDocument();
  });

  it("hides Continue when the backend refuses it — the mutation guard for the capability change", () => {
    // Revert RunActionCapabilityResolver's park clause and the projected action comes back disabled: this must go red.
    renderActions([action({ enabled: false, disabledReason: "Only a stopped, failed, or completion-parked turn can be resumed in place." })]);
    expect(screen.queryByRole("button", { name: /Continue/ })).toBeNull();
  });
});

/**
 * A park is `Suspended`, which `isRunActive` calls LIVE — correctly, since the run can still change. But every live
 * AFFORDANCE then claimed work was happening: a pulsing sticky bar hardcoding "Working", and a "running 3h12m" that
 * grew on every poll, both sitting under a header that already read "Parked". The bar stays (it hosts the Stop the
 * park card offers); its claims come from the backend.
 */
const NOW = Date.parse("2026-09-06T12:00:00Z");
const parkedTurn = (over: Partial<AssistantTurnBlock> = {}) =>
  turn({ statusWord: "Parked", parkedAt: "2026-09-06T09:00:00Z", durationMs: 7_200_000, at: "2026-09-06T07:00:00Z", ...over });

describe("a parked turn's live affordances", () => {
  it("names the park in the sticky bar and counts how long it has been parked", () => {
    render(<LiveRunBar turn={parkedTurn()} nowMs={NOW} />);

    expect(screen.getByText("Parked")).toBeInTheDocument();
    expect(screen.getByText("parked for 3h 0m")).toBeInTheDocument();
    expect(screen.queryByText(/running/)).toBeNull();
  });

  it("keeps the working bar exactly as it was for an ordinary live turn", () => {
    const { container } = render(<LiveRunBar turn={turn({ status: "Running", durationMs: 7_200_000 })} nowMs={NOW} />);

    expect(screen.getByText("Working")).toBeInTheDocument();
    expect(screen.getByText("running 2h 0m")).toBeInTheDocument();
    expect(container.querySelector(".room-livebar-parked")).toBeNull();
  });

  it("stops the pulse — the room's one live signal must not fire over a run that stopped", () => {
    const { container } = render(<LiveRunBar turn={parkedTurn()} nowMs={NOW} />);
    expect(container.querySelector(".room-livebar-parked")).not.toBeNull();
  });

  it("drops the 'running' prefix from the meta line, whose duration the backend already froze", () => {
    // Mutation guard: revert either the FE gate or RoomProjector's frozen DurationMs and this reads "running 2h 0m".
    expect(turnMeta(parkedTurn(), NOW, true)).toContain("2h 0m");
    expect(turnMeta(parkedTurn(), NOW, true)).not.toContain("running");
    expect(turnMeta(turn({ status: "Running", durationMs: 7_200_000 }), NOW, true)).toContain("running 2h 0m");
  });
});

describe("turnHeaderWord", () => {
  it("prefers the backend's word so a park never reads like an approval wait", () => {
    expect(turnHeaderWord(turn({ statusWord: "Parked" }), true)).toBe("Parked");
    expect(turnHeaderWord(turn({ statusWord: "Parked" }), false)).toBe("Parked");
  });

  it("speaks for an attempt-ladder rung too, so the chip cannot disagree with the header above it", () => {
    expect(turnHeaderWord({ status: "Suspended", statusWord: "Parked" }, false)).toBe("Parked");
    expect(turnHeaderWord({ status: "Suspended" }, false)).toBe("Waiting");
  });

  it("keeps the shared lexicon for every ordinary turn", () => {
    expect(turnHeaderWord(turn(), true)).toBe("Waiting");
    expect(turnHeaderWord(turn({ status: "Failure" }), false)).toBe("Failed");
    expect(turnHeaderWord(turn({ status: "Success", statusWord: "   " }), false)).toBe("Done");
  });
});
