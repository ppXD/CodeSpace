import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { InteractionResolution, InteractionState, MessageInteractionView } from "@/api/chat";
import type { TeamMemberSummary } from "@/api/teams";

import { MessageInteractionCard } from "./MessageInteractionCard";

const members = new Map<string, TeamMemberSummary>([
  ["rev", { userId: "rev", name: "Alice", email: "a@x", avatarUrl: null }],
]);

function card(state: InteractionState, resolution: InteractionResolution | null = null): MessageInteractionView {
  return {
    version: 1,
    component: {
      kind: "action_buttons",
      buttons: [
        { key: "approve", label: "Approve", style: "Primary", requiresComment: false },
        { key: "request_changes", label: "Request changes", style: "Danger", requiresComment: true },
      ],
    },
    allowedResponderUserIds: null,
    state,
    resolution,
  };
}

describe("MessageInteractionCard", () => {
  it("renders each button with its style class while the card is Open", () => {
    render(<MessageInteractionCard interaction={card("Open")} members={members} />);

    const approve = screen.getByRole("button", { name: "Approve" });
    const reject = screen.getByRole("button", { name: "Request changes" });
    expect(approve.className).toContain("btn-primary");
    expect(reject.className).toContain("btn-danger");
  });

  it("disables the buttons while read-only (not yet wired to respond)", () => {
    render(<MessageInteractionCard interaction={card("Open")} members={members} />);
    for (const b of screen.getAllByRole("button")) expect(b).toBeDisabled();
  });

  it("shows the chosen action's label + responder + comment once resolved, hiding the buttons", () => {
    const resolution: InteractionResolution = { responseKey: "approve", byUserId: "rev", comment: "looks good", atUtc: "2026-06-01T09:00:00Z" };
    render(<MessageInteractionCard interaction={card("Resolved", resolution)} members={members} />);

    expect(screen.getByText("Approve")).toBeInTheDocument();      // label, resolved from the response key
    expect(screen.getByText("by Alice")).toBeInTheDocument();     // responder resolved via the identity map
    expect(screen.getByText("“looks good”")).toBeInTheDocument(); // the reviewer's comment
    expect(screen.queryByRole("button")).toBeNull();              // settled card shows the outcome, not the buttons
  });

  it("falls back to the raw response key when no button matches (forward-compat)", () => {
    const resolution: InteractionResolution = { responseKey: "ghost_action", byUserId: "rev", comment: null, atUtc: "2026-06-01T09:00:00Z" };
    render(<MessageInteractionCard interaction={card("Resolved", resolution)} members={members} />);
    expect(screen.getByText("ghost_action")).toBeInTheDocument();
  });

  it("renders an Expired card with no resolution as a muted stamp", () => {
    render(<MessageInteractionCard interaction={card("Expired")} members={members} />);
    expect(screen.getByText("Expired")).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
  });
});
