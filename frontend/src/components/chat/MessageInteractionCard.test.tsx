import { fireEvent, render, screen } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { InteractionResolution, InteractionResponse, InteractionState, MessageInteractionView, ResolvePolicy } from "@/api/chat";
import { ApiError } from "@/api/request";
import type { TeamMemberSummary } from "@/api/teams";

import { MessageInteractionCard } from "./MessageInteractionCard";

// The card owns the respond mutation; stub it so we can assert what a click sends without a network.
const mutate = vi.fn();
let pending = false;
vi.mock("@/hooks/use-chat", () => ({ useRespondToMessage: () => ({ mutate, isPending: pending }) }));

// The card routes a 428 actor_identity_required to the global gate; stub it to capture the hand-off.
const prompt = vi.fn(() => true);
vi.mock("@/components/identities/ActorIdentityGate", () => ({ useActorIdentityGate: () => ({ prompt }) }));

beforeEach(() => { mutate.mockClear(); prompt.mockClear(); pending = false; });

const members = new Map<string, TeamMemberSummary>([
  ["rev", { userId: "rev", name: "Alice", email: "a@x", avatarUrl: null, isBot: false }],
  ["bob", { userId: "bob", name: "Bob", email: "b@x", avatarUrl: null, isBot: false }],
]);

function card(state: InteractionState, resolution: InteractionResolution | null = null, allowed: string[] | null = null, responses: InteractionResponse[] = [], resolve: ResolvePolicy = { kind: "First", count: 1 }): MessageInteractionView {
  return {
    version: 1,
    component: {
      kind: "action_buttons",
      buttons: [
        { key: "approve", label: "Approve", style: "Primary", requiresComment: false },
        { key: "request_changes", label: "Request changes", style: "Danger", requiresComment: true, vetoes: true },
      ],
    },
    allowedResponderUserIds: allowed,
    resolve,
    responses,
    state,
    resolution,
  };
}

function formCard(state: InteractionState, resolution: InteractionResolution | null = null): MessageInteractionView {
  return {
    version: 1,
    component: {
      kind: "form",
      fields: { type: "object", properties: { decision: { type: "string", enum: ["approve", "reject"] } }, required: ["decision"] },
      submitLabel: "Send",
    },
    allowedResponderUserIds: null,
    resolve: { kind: "First", count: 1 },
    responses: [],
    state,
    resolution,
  };
}

function renderCard(interaction: MessageInteractionView, myUserId: string | null = "rev") {
  return render(<MessageInteractionCard interaction={interaction} members={members} conversationId="c1" messageId="m1" myUserId={myUserId} />);
}

describe("MessageInteractionCard", () => {
  it("renders each button with its style class while the card is Open", () => {
    renderCard(card("Open"));
    expect(screen.getByRole("button", { name: "Approve" }).className).toContain("btn-primary");
    expect(screen.getByRole("button", { name: "Request changes" }).className).toContain("btn-danger");
  });

  it("submits immediately with no comment for a plain button", () => {
    renderCard(card("Open"));
    fireEvent.click(screen.getByRole("button", { name: "Approve" }));
    expect(mutate).toHaveBeenCalledWith({ messageId: "m1", responseKey: "approve", comment: null }, expect.anything());
  });

  it("routes a 428 actor_identity_required to the gate, with a retry of the same response", () => {
    const err = new ApiError(428, "actor_identity_required", "link required", { provider: "GitLab", providerInstanceId: "i1" });
    mutate.mockImplementation((_vars, opts) => opts?.onError?.(err));
    renderCard(card("Open"));

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    expect(prompt).toHaveBeenCalledWith(err, expect.any(Function));
  });

  it("shows a 403 actor_repo_permission_denied inline and does NOT route to the identity gate (card stays open)", () => {
    const err = new ApiError(403, "actor_repo_permission_denied", "denied", {
      provider: "GitLab",
      repository: "acme/api",
      message: "Your GitLab role on this project is Reporter — reviewing needs Developer or higher.",
    });
    mutate.mockImplementation((_vars, opts) => opts?.onError?.(err));
    renderCard(card("Open"));

    fireEvent.click(screen.getByRole("button", { name: "Approve" }));

    // The reason shows inline (nothing to LINK → no modal), and the card stays Open for a retry.
    expect(screen.getByRole("alert")).toHaveTextContent("Reporter");
    expect(prompt).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument();
  });

  it("collects a comment before submitting a requires-comment button", () => {
    renderCard(card("Open"));

    fireEvent.click(screen.getByRole("button", { name: "Request changes" }));
    expect(mutate).not.toHaveBeenCalled();   // reveals the composer first, doesn't submit yet

    // The submit button is disabled until a non-empty comment is entered.
    const submit = screen.getByRole("button", { name: "Request changes" });
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getByRole("textbox"), { target: { value: "  please add a test  " } });
    expect(submit).toBeEnabled();
    fireEvent.click(submit);

    expect(mutate).toHaveBeenCalledWith({ messageId: "m1", responseKey: "request_changes", comment: "please add a test" }, expect.anything());
  });

  it("can cancel out of the comment composer without submitting", () => {
    renderCard(card("Open"));
    fireEvent.click(screen.getByRole("button", { name: "Request changes" }));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect(screen.getByRole("button", { name: "Approve" })).toBeInTheDocument();   // back to the button row
    expect(mutate).not.toHaveBeenCalled();
  });

  it("disables the buttons while a response is in flight", () => {
    pending = true;
    renderCard(card("Open"));
    for (const b of screen.getAllByRole("button")) expect(b).toBeDisabled();
  });

  it("disables responding and hints when the viewer is not an allowed responder", () => {
    renderCard(card("Open", null, ["someone-else"]), "rev");
    expect(screen.getByRole("button", { name: "Approve" })).toBeDisabled();
    expect(screen.getByText(/Only the requested reviewer can decide/)).toBeInTheDocument();
  });

  it("shows the chosen action's label + responder + comment once resolved, hiding the buttons", () => {
    const resolution: InteractionResolution = { responseKey: "approve", byUserId: "rev", comment: "looks good", values: null, atUtc: "2026-06-01T09:00:00Z" };
    renderCard(card("Resolved", resolution));

    expect(screen.getByText("Approve")).toBeInTheDocument();
    expect(screen.getByText("by Alice")).toBeInTheDocument();
    expect(screen.getByText("“looks good”")).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("falls back to the raw response key when no button matches (forward-compat)", () => {
    const resolution: InteractionResolution = { responseKey: "ghost_action", byUserId: "rev", comment: null, values: null, atUtc: "2026-06-01T09:00:00Z" };
    renderCard(card("Resolved", resolution));
    expect(screen.getByText("ghost_action")).toBeInTheDocument();
  });

  it("renders a form card's fields and gates submit until required fields are filled", () => {
    renderCard(formCard("Open"));

    const submit = screen.getByRole("button", { name: "Send" });
    expect(submit).toBeDisabled();   // required 'decision' not yet chosen

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "approve" } });
    expect(submit).toBeEnabled();
  });

  it("submits a form with the entered values", () => {
    renderCard(formCard("Open"));

    fireEvent.change(screen.getByRole("combobox"), { target: { value: "reject" } });
    fireEvent.click(screen.getByRole("button", { name: "Send" }));

    expect(mutate).toHaveBeenCalledWith({ messageId: "m1", responseKey: "submit", comment: null, values: { decision: "reject" } }, expect.anything());
  });

  it("shows a resolved form's submitted values, hiding the fields", () => {
    const resolution: InteractionResolution = { responseKey: "submit", byUserId: "rev", comment: null, values: { decision: "approve" }, atUtc: "2026-06-01T09:00:00Z" };
    renderCard(formCard("Resolved", resolution));

    expect(screen.getByText("Submitted")).toBeInTheDocument();
    expect(screen.getByText("decision: approve")).toBeInTheDocument();
    expect(screen.queryByRole("combobox")).toBeNull();
  });

  it("renders an Expired card with no resolution as a muted stamp", () => {
    renderCard(card("Expired"));
    expect(screen.getByText("Expired")).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("renders the response timeline — a comment with its author's name", () => {
    const responses: InteractionResponse[] = [
      { byUserId: "bob", kind: "Comment", key: null, comment: "looks risky to me", atUtc: "2026-06-01T09:00:00Z" },
    ];
    renderCard(card("Open", null, null, responses));
    expect(screen.getByText("looks risky to me")).toBeInTheDocument();
    expect(screen.getByText("Bob")).toBeInTheDocument();
  });

  it("shows a live quorum tally for the leading action key", () => {
    const responses: InteractionResponse[] = [
      { byUserId: "rev", kind: "Action", key: "approve", comment: null, atUtc: "2026-06-01T09:00:00Z" },
    ];
    renderCard(card("Open", null, null, responses, { kind: "Quorum", count: 2 }));
    expect(screen.getByText(/1 \/ 2 approved/)).toBeInTheDocument();
  });

  it("lets any member add a non-terminal comment via the comment box (trimmed, reserved key)", () => {
    renderCard(card("Open"));
    fireEvent.change(screen.getByLabelText("Add a comment"), { target: { value: "  ship it  " } });
    fireEvent.click(screen.getByRole("button", { name: "Comment" }));
    expect(mutate).toHaveBeenCalledWith({ messageId: "m1", responseKey: "__comment__", comment: "ship it" }, expect.anything());
  });
});
