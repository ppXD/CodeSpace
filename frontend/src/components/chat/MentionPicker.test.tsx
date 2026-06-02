import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import type { TeamMemberSummary } from "@/api/teams";

import { MentionPicker } from "./MentionPicker";

const member = (userId: string, name: string): TeamMemberSummary => ({ userId, name, email: `${name}@x`, avatarUrl: null, isBot: false });
const roster = [member("u1", "Alice"), member("u2", "Bob")];

describe("MentionPicker", () => {
  it("renders nothing when there are no candidates", () => {
    const { container } = render(<MentionPicker candidates={[]} activeIndex={0} onPick={vi.fn()} onHover={vi.fn()} />);
    expect(container.firstChild).toBeNull();
  });

  it("lists candidates and marks the active row", () => {
    render(<MentionPicker candidates={roster} activeIndex={1} onPick={vi.fn()} onHover={vi.fn()} />);

    expect(screen.getByText("Alice")).toBeInTheDocument();
    const options = screen.getAllByRole("option");
    expect(options[1]).toHaveAttribute("aria-selected", "true");
    expect(options[0]).toHaveAttribute("aria-selected", "false");
  });

  it("picks a member on mousedown (preventing default so the editor keeps focus)", () => {
    const onPick = vi.fn();
    render(<MentionPicker candidates={roster} activeIndex={0} onPick={onPick} onHover={vi.fn()} />);

    fireEvent.mouseDown(screen.getByText("Bob"));
    expect(onPick).toHaveBeenCalledWith(roster[1]);
  });

  it("reports the hovered row index", () => {
    const onHover = vi.fn();
    render(<MentionPicker candidates={roster} activeIndex={0} onPick={vi.fn()} onHover={onHover} />);

    fireEvent.mouseEnter(screen.getByText("Bob").closest("button")!);
    expect(onHover).toHaveBeenCalledWith(1);
  });
});
