import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { TeamMemberSummary } from "@/api/teams";

import { MessageBody } from "./MessageBody";

const members = new Map<string, TeamMemberSummary>([
  ["u1", { userId: "u1", name: "Alice", email: "a@x", avatarUrl: null, isBot: false, role: "Member" as const, joinedAt: null }],
]);

describe("MessageBody", () => {
  it("renders plain text unchanged", () => {
    render(<MessageBody body="hello world" members={members} myUserId={null} />);
    expect(screen.getByText("hello world")).toBeInTheDocument();
  });

  it("renders a user chip with its label, prefixed with @", () => {
    render(<MessageBody body="hi <user:u1|Alice>!" members={members} myUserId={null} />);
    expect(screen.getByText("@Alice")).toBeInTheDocument();
  });

  it("resolves a labelless user chip to the member's name (not the raw id)", () => {
    render(<MessageBody body="ping <user:u1>" members={members} myUserId={null} />);
    expect(screen.getByText("@Alice")).toBeInTheDocument();
    expect(screen.queryByText(/u1/)).not.toBeInTheDocument();
  });

  it("renders a non-user reference with its label and no @ prefix", () => {
    render(<MessageBody body="see <pull_request:r#1|PR 1>" members={members} myUserId={null} />);
    const chip = screen.getByText("PR 1");
    expect(chip).toBeInTheDocument();
    expect(chip.textContent).not.toContain("@");
  });

  it("flags an @mention of the current user as a self-mention chip", () => {
    render(<MessageBody body="hi <user:u1|Alice>" members={members} myUserId="u1" />);
    expect(screen.getByText("@Alice")).toHaveAttribute("data-me", "true");
  });

  it("shows a tool-approval card exactly as the server wrote it: plain text, and a broken reference token mentions no one", () => {
    // The approval card McpRequestHandler posts is plain text built for this renderer (no markdown escapes, fences or
    // emphasis), and ToolCallPreviews breaks a model-written <type:id|label> token by turning its "<" into "‹".
    const card = [
      "Agent run r1 requests approval to run git.merge_pr (Merges an open pull/merge request).",
      "",
      "- repository (bound, writable): acme/api",
      "- head: outsider/api:release — outside this run's repositories",
      "- commitTitle: ‹user:u1|Security Team> approved this",
      "",
      "Approve to let it proceed, or reject to refuse it.",
    ].join("\n");

    const { container } = render(<MessageBody body={card} members={members} myUserId={null} />);

    expect(container.querySelector(".chat-msg-text")?.textContent).toBe(card);
    expect(container.querySelector(".chat-ref")).toBeNull();
  });

  it("does not flag a mention of someone else", () => {
    render(<MessageBody body="hi <user:u1|Alice>" members={members} myUserId="someone-else" />);
    expect(screen.getByText("@Alice")).not.toHaveAttribute("data-me");
  });
});
