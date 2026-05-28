import { useState, type KeyboardEvent } from "react";

/**
 * Message input. Enter sends; Shift+Enter inserts a newline (the standard chat affordance).
 * Whitespace-only input can't be sent. Clears on send. Stateless about delivery — the parent
 * owns the mutation and passes <c>disabled</c> while a send is in flight.
 */
export function MessageComposer({ onSend, disabled, placeholder }: { onSend: (body: string) => void; disabled?: boolean; placeholder?: string }) {
  const [text, setText] = useState("");

  const send = () => {
    const body = text.trim();
    if (!body || disabled) return;

    onSend(body);
    setText("");
  };

  const onKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      send();
    }
  };

  return (
    <div className="chat-composer">
      <textarea
        className="chat-composer-input"
        value={text}
        rows={1}
        placeholder={placeholder ?? "Write a message…"}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKeyDown}
      />
      <button className="btn btn-primary" onClick={send} disabled={disabled || text.trim().length === 0}>
        Send
      </button>
    </div>
  );
}
