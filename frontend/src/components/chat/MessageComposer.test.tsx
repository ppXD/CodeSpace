import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { MessageComposer } from "./MessageComposer";

/**
 * The composer is a contenteditable editor (not a textarea), so tests drive it by setting innerHTML
 * + firing an input event rather than changing a `value`. The caret-bound picker insertion is
 * browser-only (verified live); its parsing/filtering is covered in lib/mentionInput + MentionPicker.
 */
vi.mock("@/hooks/use-team-members", () => ({
  useTeamMembers: () => ({ data: [{ userId: "u1", name: "Alice", email: "a@x", avatarUrl: null }] }),
}));

function setContent(editor: HTMLElement, html: string) {
  editor.innerHTML = html;
  fireEvent.input(editor);
}

describe("MessageComposer", () => {
  it("sends trimmed text on click and clears the editor", () => {
    const onSend = vi.fn();
    render(<MessageComposer onSend={onSend} />);

    const editor = screen.getByRole("textbox");
    setContent(editor, "  hello  ");
    fireEvent.click(screen.getByRole("button", { name: /send/i }));

    expect(onSend).toHaveBeenCalledWith("hello");
    expect(editor.textContent).toBe("");
  });

  it("serializes a mention chip to its generic token on send", () => {
    const onSend = vi.fn();
    render(<MessageComposer onSend={onSend} />);

    const editor = screen.getByRole("textbox");
    setContent(editor, 'hi <span data-ref-type="user" data-ref-id="u1" data-label="Alice">@Alice</span> there');
    fireEvent.click(screen.getByRole("button", { name: /send/i }));

    expect(onSend).toHaveBeenCalledWith("hi <user:u1|Alice> there");
  });

  it("sends on Enter but not on Shift+Enter", () => {
    const onSend = vi.fn();
    render(<MessageComposer onSend={onSend} />);

    const editor = screen.getByRole("textbox");
    setContent(editor, "hi");

    fireEvent.keyDown(editor, { key: "Enter", shiftKey: true });
    expect(onSend).not.toHaveBeenCalled();

    fireEvent.keyDown(editor, { key: "Enter" });
    expect(onSend).toHaveBeenCalledWith("hi");
  });

  it("does not send on the Enter that commits an IME composition, but sends the next Enter", () => {
    const onSend = vi.fn();
    render(<MessageComposer onSend={onSend} />);

    const editor = screen.getByRole("textbox");
    setContent(editor, "你好");

    fireEvent.compositionStart(editor);
    fireEvent.keyDown(editor, { key: "Enter" });
    expect(onSend).not.toHaveBeenCalled();

    fireEvent.compositionEnd(editor);
    fireEvent.keyDown(editor, { key: "Enter" });
    expect(onSend).toHaveBeenCalledWith("你好");
  });

  it("never sends whitespace-only input", () => {
    const onSend = vi.fn();
    render(<MessageComposer onSend={onSend} />);

    const editor = screen.getByRole("textbox");
    setContent(editor, "   ");
    fireEvent.keyDown(editor, { key: "Enter" });

    expect(onSend).not.toHaveBeenCalled();
  });
});
