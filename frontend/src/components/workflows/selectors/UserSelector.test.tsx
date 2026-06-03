import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { UserMultiSelector } from "./UserSelector";

// A 20-member roster so the cap (MAX_VISIBLE = 8) and the filter are exercised on a realistically large team.
const MANY = Array.from({ length: 20 }, (_, i) => ({ userId: `u${i}`, name: `User ${i}`, email: `u${i}@x`, avatarUrl: null, isBot: false }));
const mockMembers = { isLoading: false, data: MANY };
vi.mock("@/hooks/use-team-members", () => ({ useTeamMembers: () => mockMembers }));

function setup(value: string[] = []) {
  const onChange = vi.fn();
  render(<UserMultiSelector value={value} onChange={onChange} />);
  return { onChange, input: screen.getByRole("textbox", { name: "Search members" }) };
}

describe("UserMultiSelector — searchable user combobox (scales past a flat chip list)", () => {
  it("shows the 'anyone' hint and no dropdown until the input is focused", () => {
    setup([]);
    expect(screen.getByText(/anyone in the conversation/i)).toBeInTheDocument();
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("caps the dropdown at 8 and shows a '+N more' hint instead of every member", () => {
    const { input } = setup([]);
    fireEvent.focus(input);
    expect(screen.getAllByRole("option")).toHaveLength(8);          // not 20
    expect(screen.getByText(/\+12 more/)).toBeInTheDocument();
  });

  it("filters the candidates by the typed query", () => {
    const { input } = setup([]);
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: "User 13" } });
    const opts = screen.getAllByRole("option");
    expect(opts).toHaveLength(1);
    expect(opts[0]).toHaveTextContent("User 13");
  });

  it("adds the picked member to the value array", () => {
    const { onChange, input } = setup(["u0"]);
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: "User 5" } });
    fireEvent.mouseDown(screen.getByRole("option", { name: /User 5/ }));
    expect(onChange).toHaveBeenCalledWith(["u0", "u5"]);
  });

  it("excludes already-selected members from the dropdown", () => {
    const { input } = setup(["u0"]);
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: "User 0" } });        // only "User 0" matches — and it's selected
    expect(screen.queryByRole("option")).toBeNull();
    expect(screen.getByText(/no matching member/i)).toBeInTheDocument();
  });

  it("renders a removable tag per selected member and removes on click", () => {
    const { onChange } = setup(["u0", "u1"]);
    expect(screen.getByText("User 0")).toBeInTheDocument();
    expect(screen.getByText("User 1")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "Remove User 0" }));
    expect(onChange).toHaveBeenCalledWith(["u1"]);
  });

  it("removes the last tag on Backspace when the query is empty", () => {
    const { onChange, input } = setup(["u0", "u1"]);
    fireEvent.keyDown(input, { key: "Backspace" });
    expect(onChange).toHaveBeenCalledWith(["u0"]);
  });

  it("navigates with ArrowDown and picks the active option on Enter", () => {
    const { onChange, input } = setup([]);
    fireEvent.focus(input);
    fireEvent.keyDown(input, { key: "ArrowDown" });   // active 0 → 1
    fireEvent.keyDown(input, { key: "ArrowDown" });   // 1 → 2 (visible[2] = u2)
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onChange).toHaveBeenCalledWith(["u2"]);
  });

  it("closes the dropdown on Escape", () => {
    const { input } = setup([]);
    fireEvent.focus(input);
    expect(screen.getByRole("listbox")).toBeInTheDocument();
    fireEvent.keyDown(input, { key: "Escape" });
    expect(screen.queryByRole("listbox")).toBeNull();
  });

  it("switches the hint to a count once members are selected", () => {
    setup(["u0", "u1", "u2"]);
    expect(screen.getByText(/3 selected/)).toBeInTheDocument();
  });
});
