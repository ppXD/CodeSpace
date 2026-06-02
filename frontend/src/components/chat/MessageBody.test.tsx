import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import type { TeamMemberSummary } from "@/api/teams";

import { MessageBody } from "./MessageBody";

const members = new Map<string, TeamMemberSummary>([
  ["u1", { userId: "u1", name: "Alice", email: "a@x", avatarUrl: null, isBot: false }],
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

  it("does not flag a mention of someone else", () => {
    render(<MessageBody body="hi <user:u1|Alice>" members={members} myUserId="someone-else" />);
    expect(screen.getByText("@Alice")).not.toHaveAttribute("data-me");
  });
});
