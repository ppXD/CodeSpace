import { fireEvent, render, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { MessageComposer } from "./MessageComposer";

describe("MessageComposer", () => {
  it("sends trimmed text on click and clears the input", () => {
    const onSend = vi.fn();
    render(<MessageComposer onSend={onSend} />);

    const input = screen.getByRole("textbox") as HTMLTextAreaElement;
    fireEvent.change(input, { target: { value: "  hello  " } });
    fireEvent.click(screen.getByRole("button", { name: /send/i }));

    expect(onSend).toHaveBeenCalledWith("hello");
    expect(input.value).toBe("");
  });

  it("sends on Enter but inserts a newline on Shift+Enter", () => {
    const onSend = vi.fn();
    render(<MessageComposer onSend={onSend} />);

    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "hi" } });

    fireEvent.keyDown(input, { key: "Enter", shiftKey: true });
    expect(onSend).not.toHaveBeenCalled();

    fireEvent.keyDown(input, { key: "Enter" });
    expect(onSend).toHaveBeenCalledWith("hi");
  });

  it("does not send on the Enter that commits an IME composition, but sends the next Enter", () => {
    const onSend = vi.fn();
    render(<MessageComposer onSend={onSend} />);

    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "你好" } });

    // Mid-composition (choosing a CJK candidate): Enter commits the candidate into the box, not a send.
    fireEvent.compositionStart(input);
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onSend).not.toHaveBeenCalled();

    // Composition finished → the next Enter actually sends.
    fireEvent.compositionEnd(input);
    fireEvent.keyDown(input, { key: "Enter" });
    expect(onSend).toHaveBeenCalledWith("你好");
  });

  it("never sends whitespace-only input", () => {
    const onSend = vi.fn();
    render(<MessageComposer onSend={onSend} />);

    const input = screen.getByRole("textbox");
    fireEvent.change(input, { target: { value: "   " } });
    fireEvent.keyDown(input, { key: "Enter" });

    expect(onSend).not.toHaveBeenCalled();
  });
});
